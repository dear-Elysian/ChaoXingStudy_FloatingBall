using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace CxBall.Shell;

/// <summary>Java 核心返回的业务错误（附录 B 错误码）。</summary>
public sealed class CoreException : Exception
{
    public string Code { get; }
    public bool Retryable { get; }
    public CoreException(string code, string message, bool retryable) : base(message)
    { Code = code; Retryable = retryable; }
}

/// <summary>
/// Java 核心进程管理 + stdio JSON-Lines 客户端（附录 B）。
/// 请求 {id,type,ts,payload}；响应同 id 带 ok；事件带 for → OnEvent 分发。
/// </summary>
public sealed class JavaCore : IDisposable
{
    public const int DefaultTimeoutMs = 30_000;
    public const int FetchTimeoutMs   = 120_000;

    Process? _proc;
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    readonly object _writeLock = new();
    readonly object _startLock = new();
    int _seq;
    int _restartAttempts;
    bool _disposed;
    JsonArray? _lastCookies;
    string? _lastUa;
    bool _needsReauth;

    /// <summary>fetch.progress 等事件（payload 为事件 JsonObject 整体）。</summary>
    public event Action<JsonObject>? OnEvent;

    public void Start()
    {
        lock (_startLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(JavaCore));
            if (_proc is { HasExited: false }) return;
            if (_proc != null)
            {
                // 旧进程已退出但 Exited 事件尚未处理：先解绑并释放，避免句柄泄漏
                _proc.Exited -= OnProcExited;
                _proc.Dispose();
                _proc = null;
            }
            if (!File.Exists(Paths.CoreJar))
                throw new InvalidOperationException("找不到 Java 核心: " + Paths.CoreJar);

            var psi = new ProcessStartInfo
            {
                // 便携版优先用随包的 jlink 裁剪 JRE（app/jre）；开发期回落到系统 Java
                FileName = File.Exists(Paths.BundledJava) ? Paths.BundledJava : "java",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-Dfile.encoding=UTF-8");
            psi.ArgumentList.Add("-jar");
            psi.ArgumentList.Add(Paths.CoreJar);
            psi.ArgumentList.Add("--stdio");

            var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 java 进程（P0 需要系统 Java 17+）");
            proc.EnableRaisingEvents = true;
            proc.Exited += OnProcExited;
            _proc = proc;
            _needsReauth = true;
            Log($"Java 核心已启动 pid={proc.Id}");
            _ = Task.Run(() => ReadLoop(proc));
            // stderr 只写 data/logs，不参与协议；Java 侧保证日志不含 Cookie。
            _ = Task.Run(() => PumpStderr(proc));
        }
    }

    async Task ReadLoop(Process proc)
    {
        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonObject? msg = null;
                try { msg = JsonNode.Parse(line) as JsonObject; } catch { continue; }
                if (msg == null) continue;

                if (msg["for"] != null) { OnEvent?.Invoke(msg); continue; }

                var id = msg["id"]?.GetValue<string>();
                if (id != null && _pending.TryRemove(id, out var tcs))
                {
                    Interlocked.Exchange(ref _restartAttempts, 0);
                    tcs.TrySetResult(msg);
                }
            }
        }
        catch { }
        finally
        {
            // 旧进程的读循环晚于新进程启动时，不能误伤新进程的请求
            if (ReferenceEquals(_proc, proc))
                FailPending("Java 核心进程已退出");
        }
    }

    static void PumpStderr(Process proc)
    {
        try
        {
            string? line;
            while ((line = proc.StandardError.ReadLine()) != null)
                if (!string.IsNullOrWhiteSpace(line)) Log("[java] " + line);
        }
        catch { }
    }

    /// <summary>Java 异常退出后自动重启并重放最近一次 auth.setCookies（附录 B.0）。</summary>
    void OnProcExited(object? sender, EventArgs e)
    {
        if (_disposed || sender is not Process proc) return;
        lock (_startLock)
        {
            if (!ReferenceEquals(_proc, proc)) return;   // 旧进程的迟到事件
            _proc = null;
        }
        FailPending("Java 核心进程已退出");
        Log($"Java 核心已退出 code={SafeExitCode(proc)}");
        if (_restartAttempts >= 3)
        {
            Log("Java 核心连续退出 3 次，停止自动重启");
            return;
        }
        _restartAttempts++;
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            if (_disposed) return;
            try
            {
                Start();
                if (_lastCookies != null) await SetCookies(_lastCookies, _lastUa);
                Log("Java 核心已自动重启并恢复会话");
            }
            catch (Exception ex) { Log("Java 核心自动重启失败: " + ex.Message); }
        });
    }

    static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    void FailPending(string message)
    {
        foreach (var kv in _pending)
            kv.Value.TrySetException(new CoreException("INTERNAL", message, true));
        _pending.Clear();
    }

    static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Paths.LogsDir);
            var file = Path.Combine(Paths.LogsDir, "core-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            lock (StaticLogLock)
                File.AppendAllText(file, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }

    static readonly object StaticLogLock = new();

    async Task<JsonObject> RequestAsync(string type, JsonObject? payload, int timeoutMs, CancellationToken ct = default)
    {
        Start();
        // 新进程没有 Cookie：先重放最近一次会话，保证崩溃恢复后的请求仍带登录态
        if (_needsReauth && _lastCookies != null && type is not ("auth.setCookies" or "sys.handshake"))
            await SetCookies(_lastCookies, _lastUa);

        var id = "c" + Interlocked.Increment(ref _seq);
        var req = new JsonObject
        {
            ["id"] = id,
            ["type"] = type,
            ["ts"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            ["payload"] = payload ?? new JsonObject(),
        };
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        lock (_writeLock)
        {
            _proc!.StandardInput.WriteLine(req.ToJsonString());
            _proc!.StandardInput.Flush();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        using var reg = cts.Token.Register(() =>
            tcs.TrySetException(new CoreException("NETWORK_ERROR", $"等待 Java 响应超时: {type}", true)));

        var resp = await tcs.Task.ConfigureAwait(false);
        if (resp["ok"]?.GetValue<bool>() == true)
            return resp["payload"] as JsonObject ?? new JsonObject();

        var err = resp["error"] as JsonObject;
        throw new CoreException(
            err?["code"]?.GetValue<string>() ?? "INTERNAL",
            err?["message"]?.GetValue<string>() ?? "未知错误",
            err?["retryable"]?.GetValue<bool>() ?? false);
    }

    // ---------- 附录 B 消息 ----------

    public Task<JsonObject> Handshake() => RequestAsync("sys.handshake", null, DefaultTimeoutMs);

    /// <summary>cookies: [{name,value,domain,path}]，仅内存传输，禁落盘禁日志。</summary>
    public async Task<JsonObject> SetCookies(JsonArray cookies, string? ua)
    {
        // 只保留一份内存副本用于 Java 崩溃后的重放，不写盘
        _lastCookies = (JsonArray)cookies.DeepClone();
        _lastUa = ua;
        var r = await RequestAsync("auth.setCookies", new JsonObject { ["cookies"] = cookies, ["ua"] = ua }, DefaultTimeoutMs);
        _needsReauth = false;
        return r;
    }

    public async Task<bool> ValidateSession()
        => (await RequestAsync("session.validate", null, DefaultTimeoutMs))["valid"]?.GetValue<bool>() == true;

    /// <summary>抓取账号元数据（昵称/头像/学校，附录 B user.meta）；DeepClone 脱离响应树。</summary>
    public async Task<JsonObject> FetchMeta()
        => ((await RequestAsync("user.meta", null, DefaultTimeoutMs))["meta"]?.DeepClone() as JsonObject) ?? new JsonObject();

    public async Task<JsonArray> CourseList()
        // DeepClone 脱离响应树：调用方会把结果挂到别的 JsonObject 下，直接返回原节点会抛 "The node already has a parent"
        => (await RequestAsync("course.list", null, DefaultTimeoutMs))["courses"]?.DeepClone() as JsonArray ?? new JsonArray();

    public Task<JsonObject> FetchStart(JsonArray modules, JsonArray courseIds, CancellationToken ct = default)
        => RequestAsync("fetch.start", new JsonObject
        {
            ["modules"] = modules,
            ["courseIds"] = courseIds,
        }, FetchTimeoutMs, ct);

    public Task<JsonObject> FetchCancel() => RequestAsync("fetch.cancel", null, DefaultTimeoutMs);

    public void Dispose()
    {
        _disposed = true;
        Process? proc;
        lock (_startLock)
        {
            proc = _proc;
            _proc = null;
            if (proc != null) proc.Exited -= OnProcExited;
        }
        try
        {
            if (proc is { HasExited: false })
            {
                // 直接写 shutdown，避免走 RequestAsync 再次拉起进程
                var line = new JsonObject
                {
                    ["id"] = "shutdown",
                    ["type"] = "sys.shutdown",
                    ["ts"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                    ["payload"] = new JsonObject(),
                }.ToJsonString();
                lock (_writeLock)
                {
                    proc.StandardInput.WriteLine(line);
                    proc.StandardInput.Flush();
                }
                if (!proc.WaitForExit(3_000)) proc.Kill(true);
            }
        }
        catch { }
        proc?.Dispose();
    }
}
