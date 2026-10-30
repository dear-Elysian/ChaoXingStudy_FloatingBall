using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace CxBall.Shell;

/// <summary>一次对话结果：输出文本 + 用量（用于向用户展示本次消耗的 token）。</summary>
sealed record AiChatResult(string Text, int PromptTokens, int CompletionTokens);

/// <summary>
/// AI 端口（§7.9）：只描述应用需要的模型能力，不绑定任何厂商协议。
/// 后续接入 Anthropic / Gemini 等原生协议时新增适配器即可，Bridge 与前端无需改动。
/// </summary>
interface IAiProvider
{
    /// <summary>适配器标识，对应设置项 aiAdapter（当前仅 openai-compatible）。</summary>
    string Id { get; }

    /// <summary>用 GET /models 探测模型名；无法识别时返回 null（前端显示「未知模型」）。</summary>
    Task<string?> DetectModelAsync(string baseUrl, string apiKey, CancellationToken ct);

    /// <summary>用一次最小对话请求验证接口地址 + Key + 模型是否真正可用（测试模型）。</summary>
    Task TestAsync(string baseUrl, string apiKey, string model, CancellationToken ct);

    /// <summary>一次非流式对话，返回模型输出与用量（AI帮做：批量作答，尽量少消耗 token）。</summary>
    Task<AiChatResult> ChatAsync(string baseUrl, string apiKey, string model, string systemPrompt, string userPrompt, int maxTokens, bool thinking, CancellationToken ct);
}

/// <summary>
/// 接口地址归一化（§7.9）：缺协议时自动补默认协议（本机地址用 http，其余用 https）；
/// 无法归一化时抛 AI_BAD_ENDPOINT，由前端内置模态框提示用户补全协议。
/// </summary>
static class AiEndpoint
{
    public static string Normalize(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return "";
        if (s.StartsWith("//", StringComparison.Ordinal)) s = s[2..];

        if (!s.Contains("://", StringComparison.Ordinal))
        {
            var host = s.Split('/', 2)[0];
            var local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                        || host.StartsWith("127.", StringComparison.Ordinal)
                        || host.Equals("[::1]", StringComparison.Ordinal);
            s = (local ? "http://" : "https://") + s;
        }

        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Host.Length == 0)
            throw new CoreException("AI_BAD_ENDPOINT", "接口地址格式不正确，请补全协议后再试（例如 https://api.example.com/v1）", false);

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}

/// <summary>
/// OpenAI 兼容适配器：DeepSeek / OpenAI / Qwen / Moonshot / 智谱 / Ollama 等
/// 只要实现 GET {base}/models 且返回 {"data":[{"id":"..."}]} 即可被识别。
/// </summary>
sealed class OpenAiCompatibleAdapter : IAiProvider
{
    public string Id => "openai-compatible";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    /// <summary>作答请求可能包含多道简答题，单独放宽到 120s，不影响「测试模型」的 20s 超时。</summary>
    static readonly HttpClient ChatHttp = new() { Timeout = TimeSpan.FromSeconds(120) };

    public async Task<string?> DetectModelAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        string? lastError = null;
        foreach (var url in CandidateUrls(baseUrl))
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            HttpResponseMessage resp;
            try { resp = await Http.SendAsync(req, ct); }
            catch (TaskCanceledException)
            {
                throw new CoreException("AI_NETWORK", "连接接口地址超时，请检查网络或地址", false);
            }
            catch (Exception ex)
            {
                throw new CoreException("AI_NETWORK", "无法连接接口地址：" + ex.Message, false);
            }

            using (resp)
            {
                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new CoreException("AI_AUTH_FAILED", "API Key 无效或没有访问权限", false);
                if (!resp.IsSuccessStatusCode)
                {
                    lastError = "HTTP " + (int)resp.StatusCode;
                    continue;   // 兼容把完整接口路径（如 /chat/completions）填进地址的情况
                }
                return ParseModel(await resp.Content.ReadAsStringAsync(ct));
            }
        }
        throw new CoreException("AI_DETECT_FAILED", "接口未返回可识别的模型信息" + (lastError == null ? "" : "（" + lastError + "）"), false);
    }

    /// <summary>候选 /models 地址：已以 /models 结尾则直接用；填了具体接口路径则先剥回基址。</summary>
    static IEnumerable<string> CandidateUrls(string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');
        if (b.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            yield return b;
            yield break;
        }
        foreach (var suffix in new[] { "/chat/completions", "/completions", "/responses" })
        {
            if (!b.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            b = b[..^suffix.Length];
            break;
        }
        yield return b + "/models";
        if (!b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            yield return b + "/v1/models";
    }

    /// <summary>
    /// 最小对话请求（max_tokens=8）验证接口、Key、模型三者是否可用。
    /// 200 且响应含非空 choices 才算通过；401/403 → AI_AUTH_FAILED，其余 → AI_TEST_FAILED。
    /// </summary>
    public async Task TestAsync(string baseUrl, string apiKey, string model, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "ping" }),
            ["max_tokens"] = 8,
            ["temperature"] = 0,
            ["stream"] = false,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, ChatUrl(baseUrl))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, ct); }
        catch (TaskCanceledException)
        {
            throw new CoreException("AI_NETWORK", "测试请求超时，请检查网络或接口地址", false);
        }
        catch (Exception ex)
        {
            throw new CoreException("AI_NETWORK", "无法连接接口地址：" + ex.Message, false);
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CoreException("AI_AUTH_FAILED", "API Key 无效或没有访问权限", false);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = ProviderMessage(text);
                throw new CoreException("AI_TEST_FAILED",
                    "接口返回 HTTP " + (int)resp.StatusCode + (detail.Length > 0 ? "：" + detail : ""), false);
            }

            try
            {
                if (JsonNode.Parse(text) is not JsonObject obj
                    || obj["choices"] is not JsonArray choices || choices.Count == 0)
                    throw new CoreException("AI_TEST_FAILED", "接口响应不是 OpenAI 兼容格式（缺少 choices）", false);
            }
            catch (CoreException) { throw; }
            catch { throw new CoreException("AI_TEST_FAILED", "接口响应无法解析", false); }
        }
    }

    /// <summary>
    /// 一次性对话请求（非流式）：temperature=0；
    /// DeepSeek 系接口按 thinking 开关显式设置思考模式（默认关闭，节省推理 token）。
    /// </summary>
    public async Task<AiChatResult> ChatAsync(string baseUrl, string apiKey, string model,
        string systemPrompt, string userPrompt, int maxTokens, bool thinking, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }),
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0,
            ["stream"] = false,
        };
        // 仅对 DeepSeek 生效；其他 OpenAI 兼容厂商不认识该字段，避免 400
        if (baseUrl.Contains("deepseek.com", StringComparison.OrdinalIgnoreCase))
            body["thinking"] = new JsonObject { ["type"] = thinking ? "enabled" : "disabled" };

        using var req = new HttpRequestMessage(HttpMethod.Post, ChatUrl(baseUrl))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage resp;
        try { resp = await ChatHttp.SendAsync(req, ct); }
        catch (TaskCanceledException)
        {
            throw new CoreException("AI_NETWORK", "作答请求超时，请检查网络或接口地址", false);
        }
        catch (Exception ex)
        {
            throw new CoreException("AI_NETWORK", "无法连接接口地址：" + ex.Message, false);
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CoreException("AI_AUTH_FAILED", "API Key 无效或没有访问权限", false);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = ProviderMessage(text);
                throw new CoreException("AI_REQUEST_FAILED",
                    "接口返回 HTTP " + (int)resp.StatusCode + (detail.Length > 0 ? "：" + detail : ""), false);
            }

            try
            {
                if (JsonNode.Parse(text) is JsonObject obj
                    && obj["choices"] is JsonArray { Count: > 0 } choices
                    && choices[0]?["message"]?["content"]?.GetValue<string>() is string content
                    && content.Trim().Length > 0)
                {
                    var usage = obj["usage"] as JsonObject;
                    return new AiChatResult(
                        content.Trim(),
                        usage?["prompt_tokens"]?.GetValue<int>() ?? 0,
                        usage?["completion_tokens"]?.GetValue<int>() ?? 0);
                }

                // 输出被 max_tokens 截断（常见于推理型模型先思考后作答）
                if (JsonNode.Parse(text) is JsonObject cut
                    && cut["choices"] is JsonArray { Count: > 0 } cs
                    && (cs[0]?["finish_reason"]?.GetValue<string>() ?? "") == "length")
                    throw new CoreException("AI_TRUNCATED", "模型输出被长度限制截断，请减少题目数量后重试", false);
            }
            catch (CoreException) { throw; }
            catch { }
            throw new CoreException("AI_REQUEST_FAILED", "接口未返回有效答案", false);
        }
    }

    /// <summary>对话接口地址：已填 /chat/completions 直接用；误填 /models 等则剥回基址再拼接。</summary>
    static string ChatUrl(string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');
        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return b;
        foreach (var suffix in new[] { "/models", "/completions", "/responses" })
        {
            if (!b.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            b = b[..^suffix.Length];
            break;
        }
        return b + "/chat/completions";
    }

    /// <summary>提取厂商错误信息（error.message / message），截断 200 字避免提示过长。</summary>
    static string ProviderMessage(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj) return "";
            var m = (obj["error"]?["message"]?.GetValue<string>()
                     ?? obj["message"]?.GetValue<string>() ?? "").Trim();
            return m.Length > 200 ? m[..200] : m;
        }
        catch { return ""; }
    }

    static string? ParseModel(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj) return null;
            if (obj["data"] is JsonArray data)
                foreach (var m in data)
                {
                    var id = m?["id"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
                }
            if (obj["models"] is JsonArray models)
                foreach (var m in models)
                {
                    var id = m?["id"]?.GetValue<string>() ?? m?["name"]?.GetValue<string>() ?? m?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
                }
        }
        catch { }
        return null;
    }
}

/// <summary>
/// AI 应用服务（§7.9）：组合设置、BYOK 密钥与端口适配器，向 Bridge 暴露稳定接口。
/// 「AI帮做」= 只读抓题 → 一次批量请求模型 → 通过 CDP 写入作业页，不自动提交。
/// </summary>
static class AiService
{
    static readonly IAiProvider Provider = new OpenAiCompatibleAdapter();

    public static JsonObject Status()
    {
        var s = Settings.Get();
        return new JsonObject
        {
            ["enabled"] = s["aiEnabled"]?.GetValue<bool>() ?? false,
            ["endpoint"] = s["aiEndpoint"]?.GetValue<string>() ?? "",
            ["keySet"] = AiKeyStore.HasKey(),
            ["model"] = s["aiModel"]?.GetValue<string>() ?? "",
            ["thinking"] = s["aiThinking"]?.GetValue<bool>() ?? false,
            ["adapter"] = Provider.Id,
        };
    }

    public static JsonObject SetConfig(JsonObject payload)
    {
        var patch = new JsonObject();
        if (payload.ContainsKey("enabled"))
        {
            var on = payload["enabled"]?.GetValue<bool>() ?? false;
            // 启用前置条件（§7.9）：详情浏览器地址不能为空；前端同样拦截并弹窗提示
            if (on && (Settings.Get()["browserPath"]?.GetValue<string>() ?? "").Trim().Length == 0)
                throw new CoreException("BROWSER_NOT_SET", "请先在「详情浏览器」中填写浏览器地址，然后再启用 AI 模型", false);
            patch["aiEnabled"] = on;
        }
        if (payload.ContainsKey("endpoint"))
        {
            patch["aiEndpoint"] = AiEndpoint.Normalize(payload["endpoint"]?.GetValue<string>());
            patch["aiModel"] = "";   // 地址变化后旧模型名不再可信
        }
        if (payload.ContainsKey("thinking"))
            patch["aiThinking"] = payload["thinking"]?.GetValue<bool>() ?? false;
        if (patch.Count > 0) Settings.Patch(patch);
        return Status();
    }

    public static JsonObject SetKey(JsonObject payload)
    {
        var key = payload["key"]?.GetValue<string>()?.Trim() ?? "";
        if (key.Length == 0) AiKeyStore.Delete();
        else AiKeyStore.Save(key);
        Settings.Set("aiModel", "");   // Key 变化后需重新检测
        return Status();
    }

    public static async Task<JsonObject> DetectAsync()
    {
        var s = Settings.Get();
        if (s["aiEnabled"]?.GetValue<bool>() != true)
            throw new CoreException("AI_DISABLED", "请先开启「启用AI模型」", false);
        var endpoint = s["aiEndpoint"]?.GetValue<string>() ?? "";
        if (endpoint.Length == 0)
            throw new CoreException("AI_NOT_CONFIGURED", "请先填写接口地址", false);
        var key = AiKeyStore.TryLoad()
            ?? throw new CoreException("AI_NOT_CONFIGURED", "请先填写 API Key", false);

        string model;
        try { model = await Provider.DetectModelAsync(endpoint, key, CancellationToken.None) ?? ""; }
        catch (CoreException) { Settings.Set("aiModel", ""); throw; }
        Settings.Set("aiModel", model);
        return Status();
    }

    /// <summary>
    /// 测试模型（§7.9）：用最小对话请求验证接口地址 + Key + 模型是否可用。
    /// 模型名未知时先自动探测一次；成功返回模型名与实际耗时，失败抛 AI_* 错误码。
    /// </summary>
    public static async Task<JsonObject> TestAsync()
    {
        var s = Settings.Get();
        if (s["aiEnabled"]?.GetValue<bool>() != true)
            throw new CoreException("AI_DISABLED", "请先开启「启用AI模型」", false);
        var endpoint = s["aiEndpoint"]?.GetValue<string>() ?? "";
        if (endpoint.Length == 0)
            throw new CoreException("AI_NOT_CONFIGURED", "请先填写接口地址", false);
        var key = AiKeyStore.TryLoad()
            ?? throw new CoreException("AI_NOT_CONFIGURED", "请先填写 API Key", false);

        var model = s["aiModel"]?.GetValue<string>() ?? "";
        if (model.Length == 0)
        {
            model = await Provider.DetectModelAsync(endpoint, key, CancellationToken.None) ?? "";
            if (model.Length == 0)
                throw new CoreException("AI_TEST_FAILED", "无法确定测试模型，请先点击「检测模型」", false);
            Settings.Set("aiModel", model);
        }

        var sw = Stopwatch.StartNew();
        await Provider.TestAsync(endpoint, key, model, CancellationToken.None);
        sw.Stop();
        return new JsonObject { ["model"] = model, ["latencyMs"] = sw.ElapsedMilliseconds };
    }

    /// <summary>
    /// AI帮做作答请求：模型名未知时先探测一次并落盘；只发一次非流式请求以控制 token 消耗。
    /// 返回实际使用的模型名、模型输出文本与本次 token 用量。
    /// </summary>
    public static async Task<(string Model, string Text, int PromptTokens, int CompletionTokens)> ChatAsync(
        string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
    {
        EnsureReady();
        var s = Settings.Get();
        var endpoint = s["aiEndpoint"]?.GetValue<string>() ?? "";
        var key = AiKeyStore.TryLoad()!;
        var model = s["aiModel"]?.GetValue<string>() ?? "";
        if (model.Length == 0)
        {
            try { model = await Provider.DetectModelAsync(endpoint, key, ct) ?? ""; }
            catch (CoreException) { throw; }
            if (model.Length == 0)
                throw new CoreException("AI_MODEL_UNKNOWN", "无法识别模型，请先在设置页点击「检测模型」", false);
            Settings.Set("aiModel", model);
        }
        var thinking = s["aiThinking"]?.GetValue<bool>() ?? false;
        var r = await Provider.ChatAsync(endpoint, key, model, systemPrompt, userPrompt, maxTokens, thinking, ct);
        return (model, r.Text, r.PromptTokens, r.CompletionTokens);
    }

    /// <summary>AI帮做前置校验：开关、地址、Key 任一缺失都返回明确错误码供前端引导。</summary>
    public static void EnsureReady()
    {
        var s = Settings.Get();
        if (s["aiEnabled"]?.GetValue<bool>() != true)
            throw new CoreException("AI_DISABLED", "AI 模型未启用，请先在设置中开启「启用AI模型」", false);
        if ((s["aiEndpoint"]?.GetValue<string>() ?? "").Length == 0)
            throw new CoreException("AI_NOT_CONFIGURED", "请先在设置中填写 AI 接口地址", false);
        if (AiKeyStore.TryLoad() == null)
            throw new CoreException("AI_NOT_CONFIGURED", "请先在设置中填写 API Key", false);
    }
}
