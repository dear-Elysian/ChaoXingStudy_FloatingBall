using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace CxBall.Shell;

/// <summary>仅统计、清理内嵌与 AI/刷课浏览器的网页缓存；保留登录态和软件数据。</summary>
static class CacheCleaner
{
    static readonly HashSet<string> CacheDirNames = new(StringComparer.OrdinalIgnoreCase)
        { "Cache", "Code Cache", "GPUCache", "Media Cache", "ShaderCache" };
    static readonly object CleanupLock = new();
    static readonly Dictionary<int, Process> WatchedBrowsers = new();
    static int _activeTasks;
    static bool _pending;
    static string? _pendingReason;

    /// <summary>覆盖任务的启动、执行及收尾；正常结束、失败和取消都在 Dispose 时释放。</summary>
    public static IDisposable BeginActivity()
    {
        lock (CleanupLock) _activeTasks++;
        return new Activity();
    }

    sealed class Activity : IDisposable
    {
        int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (CleanupLock)
            {
                _activeTasks--;
                if (_activeTasks == 0 && _pending) QueueRetry();
            }
        }
    }

    static void QueueRetry() => _ = Task.Run(CheckAndClean);

    public static void CancelPending()
    {
        lock (CleanupLock) { _pending = false; _pendingReason = null; }
    }

    public static JsonObject Stats()
    {
        lock (CleanupLock) return new JsonObject
        {
            ["sizeMB"] = SizeMB(),
            ["lastCleanAt"] = Settings.Get()["lastCacheCleanAt"]?.GetValue<long>() ?? 0,
            ["pending"] = _pending,
            ["reason"] = _pendingReason,
        };
    }

    /// <summary>用户确认后强制请求清理；绕过时间/大小条件，仍遵守总开关与任务保护。</summary>
    public static JsonObject RequestClean()
    {
        lock (CleanupLock)
        {
            if (Settings.Get()["cacheEnabled"]?.GetValue<bool>() == false)
                return new JsonObject { ["kind"] = "disabled", ["sizeMB"] = SizeMB() };
            _pending = true;
            CheckAndClean();
            return new JsonObject
            {
                ["kind"] = _pending ? "pending" : "done",
                ["reason"] = _pendingReason,
                ["sizeMB"] = SizeMB(),
                ["lastCleanAt"] = Settings.Get()["lastCacheCleanAt"]?.GetValue<long>() ?? 0,
            };
        }
    }

    public static void CheckAndClean()
    {
        lock (CleanupLock)
        try
        {
            var s = Settings.Get();
            if (s["cacheEnabled"]?.GetValue<bool>() == false)
            {
                _pending = false;
                _pendingReason = null;
                return;
            }
            var mode = s["cacheMode"]?.GetValue<string>() ?? "time";
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            bool need = mode == "size"
                ? SizeMB() >= (s["cacheLimitMB"]?.GetValue<int>() ?? 200)
                : now - (s["lastCacheCleanAt"]?.GetValue<long>() ?? 0)
                    >= (s["cacheDays"]?.GetValue<int>() ?? 7) * 86_400_000L;
            if (!need && !_pending) return;
            _pending = true;
            // 与任务启动共用锁，避免判定后到删除前又启动新任务。
            if (_activeTasks > 0) { _pendingReason = "active_task"; return; }
            // 打开普通 Edge 不代表本程序有任务；不再用同名进程或调试端口阻止清理。
            _pendingReason = "files_in_use";
            if (Clean(Paths.DataDir, _ => true))
            {
                Settings.Set("lastCacheCleanAt", DateTimeOffset.Now.ToUnixTimeMilliseconds());
                _pending = false;
                _pendingReason = null;
            }
        }
        catch { _pendingReason = "error"; }
    }

    public static long SizeMB() => SizeBytes(Paths.DataDir) / 1_048_576;

    internal static long SizeBytes(string dataDir)
    {
        long total = 0;
        foreach (var profile in Profiles(dataDir))
            foreach (var dir in FindCacheDirs(profile))
                foreach (var file in FilesWithoutLinks(dir))
                    try { total += new FileInfo(file).Length; } catch { }
        return total;
    }

    internal static bool Clean(string dataDir, Func<string, bool> canCleanProfile)
    {
        bool complete = true;
        var root = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var profile in Profiles(dataDir))
        {
            var caches = FindCacheDirs(profile).ToList();
            if (caches.Count == 0) continue;
            if (!canCleanProfile(profile)) { complete = false; continue; }
            foreach (var dir in caches)
            {
                try
                {
                    // 删除前校验绝对路径和整棵树，绝不跟随链接清到 data 以外。
                    var target = Path.GetFullPath(dir);
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                        || HasLinks(target)) { complete = false; continue; }
                    // 浏览器可能占用少数文件，逐文件尽力清理，不能因单个锁文件保留整个缓存。
                    if (!DeleteCacheContents(target)) complete = false;
                }
                catch { complete = false; }
            }
            if (!complete && Path.GetFileName(profile).StartsWith("browser-ai-", StringComparison.OrdinalIgnoreCase))
                WatchBrowserExit(Path.GetFileName(profile)["browser-ai-".Length..]);
        }
        return complete;
    }

    static IEnumerable<string> Profiles(string dataDir)
    {
        if (!Directory.Exists(dataDir) || IsLink(dataDir)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(dataDir).Where(p => !IsLink(p)
            && (Path.GetFileName(p).Equals("browser-profile", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(p).StartsWith("browser-ai-", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; } // 文件消失或无法读取时跳过，不扩大清理范围。
    }

    static IEnumerable<string> FindCacheDirs(string profile)
    {
        var pending = new Stack<string>();
        pending.Push(profile);
        while (pending.Count > 0)
        {
            string[] children;
            try { children = Directory.GetDirectories(pending.Pop()); } catch { continue; }
            foreach (var child in children)
            {
                if (IsLink(child)) continue;
                if (Path.GetFileName(child) is "Local Storage" or "Session Storage" or "IndexedDB") continue;
                if (CacheDirNames.Contains(Path.GetFileName(child))) yield return child;
                else pending.Push(child);
            }
        }
    }

    static IEnumerable<string> FilesWithoutLinks(string dir)
    {
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(dir); } catch { yield break; }
        foreach (var entry in entries)
        {
            if (IsLink(entry)) continue;
            if (Directory.Exists(entry))
            {
                foreach (var file in FilesWithoutLinks(entry)) yield return file;
            }
            else yield return entry;
        }
    }

    static bool HasLinks(string dir)
        => IsLink(dir) || Directory.EnumerateFileSystemEntries(dir).Any(p => IsLink(p)
            || (Directory.Exists(p) && HasLinks(p)));

    static bool DeleteCacheContents(string dir)
    {
        bool complete = true;
        foreach (var file in Directory.GetFiles(dir))
            try { File.Delete(file); } catch { complete = false; }
        foreach (var child in Directory.GetDirectories(dir))
            try { if (!DeleteCacheContents(child)) complete = false; } catch { complete = false; }
        try { Directory.Delete(dir, false); } catch { complete = false; }
        return complete;
    }

    // 任务结束后专用浏览器可能仍留给用户检查答案；不强关，退出时再自动补清。
    static void WatchBrowserExit(string browser)
    {
        foreach (var process in Process.GetProcessesByName(browser))
        {
            try
            {
                var pid = process.Id;
                if (WatchedBrowsers.ContainsKey(pid)) { process.Dispose(); continue; }
                WatchedBrowsers.Add(pid, process);
                process.Exited += (_, _) =>
                {
                    lock (CleanupLock)
                    {
                        if (WatchedBrowsers.Remove(pid, out var watched)) watched.Dispose();
                        if (_pending && _activeTasks == 0) QueueRetry();
                    }
                };
                process.EnableRaisingEvents = true;
            }
            catch
            {
                try { WatchedBrowsers.Remove(process.Id); } catch { }
                process.Dispose();
            }
        }
    }
}
