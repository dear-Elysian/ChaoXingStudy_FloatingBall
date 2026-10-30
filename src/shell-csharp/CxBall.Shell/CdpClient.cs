using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace CxBall.Shell;

/// <summary>
/// 最小 Chrome DevTools Protocol 客户端（§7.9 AI帮做）：
/// 通过 WebSocket 与 Chromium 内核浏览器通信，只实现用到的 Runtime/Page/Network 三个域。
/// 连接断开时把所有未完成请求转为 AI_BROWSER_FAILED，避免界面一直等待。
/// </summary>
sealed class CdpClient : IAsyncDisposable
{
    readonly ClientWebSocket _ws;
    readonly CancellationTokenSource _cts = new();
    readonly Dictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    readonly Task _receiveLoop;
    int _seq;
    bool _closed;

    CdpClient(ClientWebSocket ws)
    {
        _ws = ws;
        _receiveLoop = ReceiveLoopAsync();
    }

    public static async Task<CdpClient> ConnectAsync(string webSocketUrl, TimeSpan timeout)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var cts = new CancellationTokenSource(timeout);
        try { await ws.ConnectAsync(new Uri(webSocketUrl), cts.Token); }
        catch (Exception ex)
        {
            ws.Dispose();
            throw new CoreException("AI_BROWSER_FAILED", "无法连接浏览器调试端口：" + ex.Message, false);
        }
        return new CdpClient(ws);
    }

    public async Task<JsonObject> CallAsync(string method, JsonObject? parameters, int timeoutMs)
    {
        if (_closed) throw new CoreException("AI_BROWSER_FAILED", "浏览器连接已断开，请重试", false);

        var id = ++_seq;
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var envelope = new JsonObject { ["id"] = id, ["method"] = method };
        if (parameters != null) envelope["params"] = parameters;
        var bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token); }
        catch (Exception ex)
        {
            _pending.Remove(id);
            throw new CoreException("AI_BROWSER_FAILED", "向浏览器发送指令失败：" + ex.Message, false);
        }

        var timeoutTask = Task.Delay(timeoutMs, _cts.Token);
        var done = await Task.WhenAny(tcs.Task, timeoutTask);
        if (done != tcs.Task)
        {
            _pending.Remove(id);
            throw new CoreException("AI_BROWSER_FAILED", "浏览器响应超时，请确认浏览器窗口未被关闭", false);
        }
        return await tcs.Task;
    }

    /// <summary>执行页面脚本并取回 JSON 结果；脚本异常转为 AI_BROWSER_FAILED。</summary>
    public async Task<JsonNode?> EvaluateAsync(string expression, int timeoutMs = 20000)
    {
        var result = await CallAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = true,
        }, timeoutMs);

        if (result["exceptionDetails"] is JsonObject ex)
        {
            var text = ex["exception"]?["description"]?.GetValue<string>()
                       ?? ex["text"]?.GetValue<string>() ?? "页面脚本执行失败";
            throw new CoreException("AI_BROWSER_FAILED", text.Length > 300 ? text[..300] : text, false);
        }
        return result["result"]?["value"];
    }

    async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult part;
                do
                {
                    part = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, part.Count));
                } while (!part.EndOfMessage);

                if (JsonNode.Parse(sb.ToString()) is not JsonObject msg) continue;
                if (msg["id"]?.GetValue<int>() is not int id) continue;   // 事件（Page/Network）忽略
                if (!_pending.Remove(id, out var tcs)) continue;

                if (msg["error"] is JsonObject err)
                {
                    var message = err["message"]?.GetValue<string>() ?? "浏览器返回错误";
                    tcs.TrySetException(new CoreException("AI_BROWSER_FAILED", message, false));
                }
                else
                {
                    tcs.TrySetResult(msg["result"] as JsonObject ?? new JsonObject());
                }
            }
        }
        catch { }
        finally
        {
            _closed = true;
            foreach (var tcs in _pending.Values)
                tcs.TrySetException(new CoreException("AI_BROWSER_FAILED", "浏览器连接已断开，请重试", false));
            _pending.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        _cts.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
        try { await _receiveLoop; } catch { }
        _ws.Dispose();
        _cts.Dispose();
    }
}
