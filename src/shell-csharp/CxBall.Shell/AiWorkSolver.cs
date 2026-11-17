using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CxBall.Shell;

/// <summary>
/// AI帮做（§7.9）：用外部 Chromium 内核浏览器打开作业作答页，只读提取题目 →
/// 一次批量请求模型 → 通过 CDP 把答案写进页面表单。不自动提交，最终由用户检查后手动提交。
///
/// 只处理单选/多选/判断/填空/简答；简答题题干区一旦含图片、附件或链接就整题跳过；
/// 其余题型不送模型。浏览器复用 <see cref="Paths.DataDir"/> 下独立 Profile，与用户日常浏览器互不影响。
/// </summary>
static class AiWorkSolver
{
    const int PreferredPort = 9333;

    // 只用 3 秒超时探测调试端口是否已就绪（浏览器启动本身另有 20 秒等待）
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>右键「AI帮做」主流程。progress 用于向前端推送阶段提示与预计耗时。</summary>
    public static async Task<JsonObject> SolveAsync(Uri url, JsonArray cookies, Action<AiProgress>? progress,
        Func<int, Task<bool>>? confirmLarge, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var exe = RequireChromiumBrowser();

        progress?.Invoke(new AiProgress("正在启动作答浏览器…"));
        var port = await EnsureBrowserAsync(exe, ct);
        ct.ThrowIfCancellationRequested();
        var wsUrl = await EnsurePageTargetAsync(port, url.AbsoluteUri, ct);
        ct.ThrowIfCancellationRequested();

        await using var cdp = await CdpClient.ConnectAsync(wsUrl, TimeSpan.FromSeconds(10));
        ct.ThrowIfCancellationRequested();
        var cookieErrors = await PrepareSessionAsync(cdp, cookies, ct);
        ct.ThrowIfCancellationRequested();

        progress?.Invoke(new AiProgress("正在打开作业页面…"));
        await cdp.CallAsync("Page.enable", null, 15000);
        ct.ThrowIfCancellationRequested();
        await cdp.CallAsync("Page.navigate", new JsonObject { ["url"] = url.AbsoluteUri }, 20000);
        ct.ThrowIfCancellationRequested();
        // 后台标签页会节流 UEditor 初始化，把作答页切到前台再等题目
        try { await cdp.CallAsync("Page.bringToFront", null, 5000); } catch (CoreException) { }

        progress?.Invoke(new AiProgress("正在等待题目加载…"));
        var page = await WaitForQuestionsAsync(cdp, 45000, ct);
        if (page["login"]?.GetValue<bool>() == true)
            throw new CoreException("SESSION_EXPIRED", "学习通登录态已失效，请先在应用内重新登录", false);
        if ((page["n"]?.GetValue<int>() ?? 0) == 0)
            throw new CoreException("AI_PAGE_TIMEOUT",
                cookieErrors > 0
                    ? "作业页面未加载出题目，且部分登录 Cookie 写入失败，请重新登录后再试"
                    : "作业页面加载超时或未找到题目，请确认该作业仍可作答", false);

        progress?.Invoke(new AiProgress("正在读取题目…"));
        var nodes = page["n"]?.GetValue<int>() ?? 0;
        // 超大作业的 DOM 遍历耗时随题量增长，提取超时跟着放宽
        var extractTimeout = Math.Max(20000, nodes * 20);
        var extracted = await cdp.EvaluateAsync(ExtractJs, extractTimeout) as JsonArray ?? new JsonArray();
        ct.ThrowIfCancellationRequested();
        var all = extracted.OfType<JsonObject>().ToList();
        var writable = all.Where(q => q["editable"]?.GetValue<bool>() == true).ToList();
        if (writable.Count == 0)
            throw new CoreException("AI_ALREADY_SUBMITTED", "该作业没有可作答的题目（可能已提交或已截止），未做任何修改", false);

        // 只保留支持的题型；简答题带附件/图片/链接的直接跳过（判定由程序完成，不送模型）
        var ask = new List<JsonObject>();
        var notes = new List<string>();
        foreach (var q in writable)
        {
            var type = q["type"]?.GetValue<string>() ?? "";
            var no = q["no"]?.GetValue<string>() ?? "";
            var tag = no.Length > 0 ? "第" + no + "题" : "该题";
            if (type.Length == 0)
            {
                notes.Add(tag + "题型无法识别，已跳过");
                continue;
            }
            if (type == "short" && (q["attach"]?.GetValue<int>() ?? 0) > 0)
            {
                notes.Add(tag + "（简答题）含图片或附件，已跳过");
                continue;
            }
            // 要求画图的简答题无法用文字答案完成，整题跳过（判定同样由程序完成）
            if (type == "short" && (q["draw"]?.GetValue<int>() ?? 0) > 0)
            {
                notes.Add(tag + "（简答题）要求画图，已跳过");
                continue;
            }
            if (type != "single" && type != "multi" && type != "judge" && type != "fill" && type != "short")
            {
                notes.Add(tag + "不在支持范围，已跳过");
                continue;
            }
            if ((q["stem"]?.GetValue<string>() ?? "").Length == 0)
            {
                notes.Add(tag + "题干为空，已跳过");
                continue;
            }
            ask.Add(q);
        }

        if (ask.Count == 0)
            throw new CoreException("AI_NO_QUESTIONS", "没有找到可交给 AI 作答的题目（仅支持选择题/判断题/填空题/简答题）", false);

        var ai = await AskAiAsync(ask, null, progress, confirmLarge, ct);
        var model = ai.Model;
        var promptTokens = ai.PromptTokens;
        var completionTokens = ai.CompletionTokens;
        var answers = ai.Answers;
        var plan = new JsonArray();
        var missing = 0;
        for (var i = 0; i < ask.Count; i++)
        {
            var q = ask[i];
            if (!answers.TryGetValue(i + 1, out var answer) || answer.Length == 0)
            {
                missing++;
                var no = q["no"]?.GetValue<string>() ?? "";
                notes.Add((no.Length > 0 ? "第" + no + "题" : "该题") + "AI 未返回答案");
                continue;
            }
            plan.Add(new JsonObject
            {
                ["qid"] = q["qid"]?.GetValue<string>() ?? "",
                ["no"] = q["no"]?.GetValue<string>() ?? "",
                ["type"] = q["type"]?.GetValue<string>() ?? "",
                ["answer"] = NormalizeAnswer(answer, q["type"]?.GetValue<string>() ?? ""),
                ["blanks"] = q["blanks"]?.GetValue<int>() ?? 0,
            });
        }

        progress?.Invoke(new AiProgress("正在填写答案…"));
        ct.ThrowIfCancellationRequested();
        // 写入超时按题量放宽（每题 50ms 起算，最少 30 秒，不设上限）
        var applyTimeout = Math.Max(30000, plan.Count * 50);
        var applied = plan.Count > 0
            ? await cdp.EvaluateAsync("(" + ApplyJs + ")(" + plan.ToJsonString() + ")", applyTimeout) as JsonArray ?? new JsonArray()
            : new JsonArray();

        var filled = 0;
        foreach (var r in applied.OfType<JsonObject>())
        {
            if (r["ok"]?.GetValue<bool>() == true) filled++;
            else
            {
                var no = r["no"]?.GetValue<string>() ?? "";
                var why = r["why"]?.GetValue<string>() ?? "写入失败";
                notes.Add((no.Length > 0 ? "第" + no + "题" : "该题") + why);
            }
        }
        var failed = applied.Count - filled;

        var message = "AI 已填写 " + filled + " 题";
        if (ask.Count < writable.Count) message += "，跳过 " + (writable.Count - ask.Count) + " 题";
        if (failed > 0) message += "，失败 " + failed + " 题";
        message += "。请在浏览器中检查答案后手动提交。";

        return new JsonObject
        {
            ["model"] = model,
            ["total"] = all.Count,
            ["writable"] = writable.Count,
            ["asked"] = ask.Count,
            ["filled"] = filled,
            ["failed"] = failed,
            ["missing"] = missing,
            ["promptTokens"] = promptTokens,
            ["completionTokens"] = completionTokens,
            ["message"] = message,
            ["notes"] = new JsonArray(notes.Take(6).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
        };
    }

    // ---------- 浏览器与调试端口 ----------

    /// <summary>校验设置里的详情浏览器可用于 AI 帮做，返回可执行文件路径。</summary>
    internal static string RequireChromiumBrowser()
    {
        var exe = BrowserLauncher.Normalize(Settings.Get()["browserPath"]?.GetValue<string>());
        if (exe.Length == 0 || !File.Exists(exe))
            throw new CoreException("BROWSER_NOT_SET", "请先在设置页的「详情浏览器」中填写浏览器地址", false);
        if (Path.GetFileName(exe).Equals("firefox.exe", StringComparison.OrdinalIgnoreCase))
            throw new CoreException("AI_UNSUPPORTED_BROWSER", "AI帮做需要 Edge/Chrome 内核浏览器，请在「详情浏览器」中改用 Edge 或 Chrome", false);
        return exe;
    }

    /// <summary>向作答浏览器注入应用保存的学习通 Cookie（先清空 Profile 内旧 Cookie）。返回失败条数。</summary>
    internal static async Task<int> PrepareSessionAsync(CdpClient cdp, JsonArray cookies, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await cdp.CallAsync("Network.enable", null, 15000);
        try { await cdp.CallAsync("Network.clearBrowserCookies", null, 15000); } catch (CoreException) { }

        var errors = 0;
        foreach (var c in cookies)
        {
            ct.ThrowIfCancellationRequested();
            if (c is not JsonObject ck) continue;
            var name = ck["name"]?.GetValue<string>() ?? "";
            var value = ck["value"]?.GetValue<string>() ?? "";
            var domain = ck["domain"]?.GetValue<string>() ?? "";
            if (name.Length == 0 || domain.Length == 0) continue;
            try
            {
                await cdp.CallAsync("Network.setCookie", new JsonObject
                {
                    ["name"] = name,
                    ["value"] = value,
                    ["domain"] = domain,
                    ["path"] = ck["path"]?.GetValue<string>() ?? "/",
                    ["secure"] = true,
                }, 15000);
            }
            catch (CoreException) { errors++; }
        }
        return errors;
    }

    static string ProfileDirFor(string exe)
        => Path.Combine(Paths.DataDir, "browser-ai-" + Path.GetFileNameWithoutExtension(exe).ToLowerInvariant());

    /// <summary>复用已运行的作答浏览器；否则用独立 Profile 启动并打开调试端口。返回端口号。</summary>
    internal static async Task<int> EnsureBrowserAsync(string exe, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var marker = LoadMarker(exe);
        if (marker != null && await IsPortAlive(marker.Value.Port))
            return marker.Value.Port;
        ct.ThrowIfCancellationRequested();

        var port = PickPort();
        var profile = ProfileDirFor(exe);
        Directory.CreateDirectory(profile);

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add("--remote-debugging-port=" + port);
        psi.ArgumentList.Add("--user-data-dir=" + profile);
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");
        psi.ArgumentList.Add("--disable-features=msEdgeFirstRunExperience");
        psi.ArgumentList.Add("--remote-allow-origins=*");
        // 刷课需要自动播放文档/视频/音频；该浏览器是自动化专用 Profile，关闭手势限制
        psi.ArgumentList.Add("--autoplay-policy=no-user-gesture-required");
        psi.ArgumentList.Add("about:blank");

        Process? started;
        try { started = Process.Start(psi); }
        catch (Exception ex)
        {
            throw new CoreException("AI_BROWSER_FAILED", "无法启动浏览器：" + ex.Message, false);
        }

        try
        {
            for (var i = 0; i < 80; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsPortAlive(port))
                {
                    SaveMarker(exe, port);
                    return port;
                }
                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 启动途中被终止：本次刚拉起的浏览器一并关掉，避免点了终止后还弹窗
            try { started?.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        throw new CoreException("AI_BROWSER_FAILED",
            "浏览器未开启调试端口。AI帮做仅支持 Edge/Chrome 内核浏览器，请在「详情浏览器」中改用 Edge 或 Chrome", false);
    }

    static async Task<bool> IsPortAlive(int port)
    {
        try
        {
            using var resp = await Http.GetAsync("http://127.0.0.1:" + port + "/json/version");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    static int PickPort()
    {
        for (var p = PreferredPort; p < PreferredPort + 30; p++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, p);
                listener.Start();
                listener.Stop();
                return p;
            }
            catch { }
        }
        throw new CoreException("AI_BROWSER_FAILED", "没有可用的本地调试端口", false);
    }

    /// <summary>新建一个空白标签页并返回其调试地址；失败返回 null。</summary>
    internal static async Task<string?> CreatePageTargetAsync(int port)
    {
        try
        {
            using var resp = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Put,
                "http://127.0.0.1:" + port + "/json/new?about:blank"));
            if (resp.IsSuccessStatusCode
                && JsonNode.Parse(await resp.Content.ReadAsStringAsync()) is JsonObject created
                && created["webSocketDebuggerUrl"]?.GetValue<string>() is string createdUrl && createdUrl.Length > 0)
                return createdUrl;
        }
        catch { }
        return null;
    }

    /// <summary>找到一个可导航的 page target；优先复用已打开同一页面的标签页。</summary>
    internal static async Task<string> EnsurePageTargetAsync(int port, string pageUrl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var list = await ReadTargetsAsync(port);
        ct.ThrowIfCancellationRequested();
        var same = list.FirstOrDefault(t => (t["url"]?.GetValue<string>() ?? "").StartsWith(pageUrl, StringComparison.OrdinalIgnoreCase));
        if (same?["webSocketDebuggerUrl"]?.GetValue<string>() is string sameUrl && sameUrl.Length > 0) return sameUrl;

        var blank = list.FirstOrDefault(t => (t["url"]?.GetValue<string>() ?? "") is "about:blank" or "");
        if (blank?["webSocketDebuggerUrl"]?.GetValue<string>() is string blankUrl && blankUrl.Length > 0) return blankUrl;

        if (await CreatePageTargetAsync(port) is string createdUrl) return createdUrl;

        var any = list.FirstOrDefault(t => t["webSocketDebuggerUrl"]?.GetValue<string>() is string s && s.Length > 0);
        if (any?["webSocketDebuggerUrl"]?.GetValue<string>() is string anyUrl) return anyUrl;
        throw new CoreException("AI_BROWSER_FAILED", "作答浏览器没有可用标签页", false);
    }

    static async Task<List<JsonObject>> ReadTargetsAsync(int port)
    {
        try
        {
            var text = await Http.GetStringAsync("http://127.0.0.1:" + port + "/json/list");
            return (JsonNode.Parse(text) as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        }
        catch { return new List<JsonObject>(); }
    }

    static (string Exe, int Port)? LoadMarker(string exe)
    {
        try
        {
            var file = MarkerFile(exe);
            if (!File.Exists(file)) return null;
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject o) return null;
            if (!string.Equals(o["exe"]?.GetValue<string>(), exe, StringComparison.OrdinalIgnoreCase)) return null;
            var port = o["port"]?.GetValue<int>() ?? 0;
            return port > 0 ? (exe, port) : null;
        }
        catch { return null; }
    }

    static void SaveMarker(string exe, int port)
    {
        try
        {
            var file = MarkerFile(exe);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, new JsonObject { ["exe"] = exe, ["port"] = port }.ToJsonString());
        }
        catch { }
    }

    static string MarkerFile(string exe)
        => Path.Combine(Paths.DataDir, "config", "ai-browser-" + Path.GetFileNameWithoutExtension(exe).ToLowerInvariant() + ".json");

    /// <summary>等待题目 DOM 出现；若跳转到登录页则提前返回。</summary>
    static async Task<JsonObject> WaitForQuestionsAsync(CdpClient cdp, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        JsonObject last = new();
        while (DateTimeOffset.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var v = await cdp.EvaluateAsync("""
                    (() => ({
                      n: document.querySelectorAll('div.questionLi').length,
                      title: document.title,
                      url: location.href,
                      login: /passport2|passport\.chaoxing/i.test(location.href)
                        || !!document.querySelector('input[name="uname"], #loginForm, .login-box')
                    }))()
                    """, 10000);
                if (v is JsonObject o) last = o;
                if ((last["n"]?.GetValue<int>() ?? 0) > 0 || last["login"]?.GetValue<bool>() == true) return last;
            }
            catch (CoreException) { /* 导航过程中执行上下文会被销毁，继续重试 */ }
            await Task.Delay(400, ct);
        }
        return last;
    }

    // ---------- 题目提取 / 答案写入（页面脚本） ----------

    /// <summary>
    /// 只读提取：返回每题的 qid/题号/题型/题干/选项/空数/附件数/是否要求画图/是否可作答。
    /// 附件判定只看题目区域本身，排除 UEditor 工具栏等编辑器 UI 自带的图片与链接。
    /// </summary>
    const string ExtractJs = """
        (() => {
          const MAP = {'单选题':'single','多选题':'multi','判断题':'judge','填空题':'fill','简答题':'short'};
          const QTYPE = {'0':'single','1':'multi','2':'fill','3':'judge','4':'short'};
          const isAttach = (x) => {
            if (x.tagName === 'IMG') { const s = x.getAttribute('src') || ''; return s.length > 0 && !/^data:/i.test(s); }
            if (x.tagName === 'A') {
              const h = (x.getAttribute('href') || '').trim();
              return h.length > 0 && !/^javascript:/i.test(h) && h !== '#';
            }
            return true;
          };
          const out = [];
          document.querySelectorAll('div.questionLi').forEach((el) => {
            const qid = el.getAttribute('data') || (el.id || '').replace('question', '');
            if (!qid) return;
            const typeName = el.getAttribute('typeName') || '';
            let type = '';
            for (const k in MAP) if (typeName.indexOf(k) >= 0) { type = MAP[k]; break; }
            if (!type) {
              const qt = el.querySelector('input[name="answertype' + qid + '"]');
              type = QTYPE[qt ? qt.value : ''] || '';
            }
            const h3 = el.querySelector('h3.mark_name');
            if (!h3) return;
            const rawStem = (h3.innerText || '').replace(/\s+/g, ' ').trim();
            const noMatch = rawStem.match(/^(\d+)/);
            let stem = rawStem.replace(/^\d+\.?\s*/, '')
                               .replace(/^[（(](单选题|多选题|判断题|填空题|简答题)[)）]\s*/, '')
                               .replace(/[（(][^)）]{0,12}分[)）]/, '')
                               .trim();
            if (stem.length > 300) stem = stem.slice(0, 300) + '…';
            // 题干出现画图类动词即视为必须作图（在截断前判断，避免漏掉后半句要求）
            const draw = /画出|画图|绘制|绘图|作图|手绘|描绘|画一下|画一个|请画/.test(rawStem) ? 1 : 0;
            const options = [];
            el.querySelectorAll('.stem_answer .answerBg').forEach((o) => {
              const num = o.querySelector('.num_option, .num_option_dx');
              const p = o.querySelector('.answer_p');
              let text = p ? (p.innerText || '').replace(/\s+/g, ' ').trim() : '';
              if (text.length > 120) text = text.slice(0, 120) + '…';
              options.push({ letter: num ? (num.getAttribute('data') || '') : '', text: text });
            });
            let blanks = 0;
            const size = el.querySelector('input[name="tiankongsize' + qid + '"]');
            if (size) blanks = parseInt(size.value || '0', 10) || 0;
            if (!blanks) blanks = el.querySelectorAll('[id^="answerEditor' + qid + '"]').length;
            // 整题只有一个 #answer<qid> 编辑器的模板（本次作业页）按 1 个空处理
            if (!blanks && el.querySelector('#answer' + qid)) blanks = 1;
            let attach = 0;
            el.querySelectorAll('img, a[href], video, audio, iframe, object, embed, input[type=file]').forEach((x) => {
              if (x.closest('.edui-editor, .edui, .edui-editor-iframeholder, .eidtDiv')) return;
              if (isAttach(x)) attach++;
            });
            const editable = !!el.querySelector('#answer' + qid)
              || el.querySelectorAll('[id^="answerEditor' + qid + '"]').length > 0
              || !!el.querySelector('.stem_answer input[type=text], .stem_answer textarea');
            out.push({
              qid: qid, no: noMatch ? noMatch[1] : '', type: type, typeName: typeName,
              stem: stem, options: options, blanks: blanks, attach: attach, draw: draw, editable: editable,
            });
          });
          return out;
        })()
        """;

    /// <summary>
    /// 写入答案：选择题/判断题复用页面自带的 addChoice / addMultipleChoice，
    /// 填空题写 UEditor 空位，简答题写 UEditor 正文；全部按页面原有事件链触发。
    /// </summary>
    const string ApplyJs = """
        async (plan) => {
          const out = [];
          const letterOf = (o) => { const n = o.querySelector('.num_option, .num_option_dx'); return n ? (n.getAttribute('data') || '') : ''; };
          const selected = (o) => !!(o.querySelector('.num_option.check_answer, .num_option_dx.check_answer_dx'));
          const pick = (opts, letter) => opts.find((o) => letterOf(o).toUpperCase() === letter);
          const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/\n/g, '<br>');
          const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
          // UEditor 是异步渲染的：实例先出现，body/iframe 稍后才就绪，过早 setContent 会抛
          // “Cannot read properties of null (reading 'style')”。写之前统一等编辑器就绪。
          // 已存在的实例优先：UE.getEditor 对不存在的 id 会新建空实例，不能盲调
          const findEditor = (id) => {
            const inst = window.UE && UE.instants ? UE.instants : {};
            for (const k in inst) {
              const ed = inst[k];
              if (ed && (ed.key === id || (ed.textarea && ed.textarea.id === id))) return ed;
            }
            return null;
          };
          const waitEditor = async (id) => {
            // 每题独立计时：共享 deadline 会让后面的题在等待超时后直接判失败
            const deadline = Date.now() + 6000;
            let ed = findEditor(id);
            if (!ed) {
              if (!document.getElementById(id)) return null;
              try { ed = window.UE && UE.getEditor ? UE.getEditor(id) : null; } catch (e) { ed = null; }
            }
            if (!ed) return null;
            while (!(ed.isReady && ed.body && ed.iframe) && Date.now() < deadline) await sleep(150);
            return (ed.body && ed.iframe) ? ed : null;
          };
          for (const p of plan) {
            const el = document.querySelector('div.questionLi[data="' + p.qid + '"]');
            if (!el) { out.push({ qid: p.qid, no: p.no, ok: false, why: '题目节点已失效' }); continue; }
            try {
              if (p.type === 'single' || p.type === 'multi' || p.type === 'judge') {
                const opts = [...el.querySelectorAll('.stem_answer .answerBg')];
                if (p.type === 'judge') {
                  const normalize = (t) => (t || '').replace(/\s/g, '');
                  let want = '';
                  if (/对|正确|true|√|是/i.test(p.answer)) want = 'true';
                  else if (/错|错误|false|×|否/i.test(p.answer)) want = 'false';
                  let target = null;
                  if (want) {
                    target = opts.find((o) => {
                      const t = normalize((o.querySelector('.answer_p') || {}).innerText || '');
                      return want === 'true' ? /对|正确|true|√|是/i.test(t) : /错|错误|false|×|否/i.test(t);
                    });
                  } else {
                    const l = (p.answer.match(/[A-Za-z]/) || [''])[0].toUpperCase();
                    if (l) target = pick(opts, l);
                  }
                  if (!target) { out.push({ qid: p.qid, no: p.no, ok: false, why: '未找到对应判断选项' }); continue; }
                  if (!selected(target)) addChoice(target);
                  const hidden = el.querySelector('#answer' + p.qid);
                  const got = hidden ? hidden.value : '';
                  out.push({ qid: p.qid, no: p.no, ok: got.length > 0, got: got });
                  continue;
                }
                let lead = (p.answer || '').match(/^([A-Za-z](?:\s*[,，、/&]?\s*[A-Za-z])*)/);
                let letters = lead ? [...new Set(lead[1].replace(/[^A-Za-z]/g, '').toUpperCase().split(''))] : [];
                if (p.type === 'single') letters = letters.slice(0, 1);
                if (letters.length === 0) { out.push({ qid: p.qid, no: p.no, ok: false, why: 'AI 未给出选项字母' }); continue; }
                let allFound = true;
                for (const l of letters) {
                  const target = pick(opts, l);
                  if (!target) { allFound = false; continue; }
                  if (!selected(target)) addChoice(target);
                }
                if (p.type === 'multi') {
                  // 多选题必须走页面自带的 addMultipleChoice；addChoice 只适用于单选
                  for (const o of opts) {
                    if (!letters.includes(letterOf(o).toUpperCase()) && selected(o)) addMultipleChoice(o);
                  }
                  for (const l of letters) {
                    const target = pick(opts, l);
                    if (target && !selected(target)) addMultipleChoice(target);
                  }
                }
                const hidden = el.querySelector('#answer' + p.qid);
                const got = (hidden ? hidden.value : '').toUpperCase();
                const gotLetters = [...new Set((got.match(/[A-Z]/g) || []))].sort().join('');
                const wantLetters = [...letters].sort().join('');
                out.push({
                  qid: p.qid, no: p.no,
                  ok: allFound && gotLetters === wantLetters,
                  got: got,
                  why: gotLetters === wantLetters ? '' : '页面未记录答案',
                });
                continue;
              }
              if (p.type === 'fill') {
                // 模板 B：整题只有一个 #answer<qid> 大编辑器（本次作业页），整段写入
                const partIds = [...el.querySelectorAll('[id^="answerEditor' + p.qid + '"]')];
                if (partIds.length === 0 && document.getElementById('answer' + p.qid)) {
                  const ed = await waitEditor('answer' + p.qid);
                  let ok = false;
                  let got = '';
                  if (ed) {
                    try {
                      ed.setContent('<p>' + esc(p.answer || '') + '</p>');
                      const txt = (ed.getContent() || '').replace(/<[^>]*>/g, '').trim();
                      ok = txt.length > 0;
                      got = '已填 ' + txt.length + ' 字';
                    } catch (e) {
                      got = '写入异常：' + (e && e.message ? e.message : e);
                    }
                  } else {
                    const ta = document.getElementById('answer' + p.qid);
                    if (ta) {
                      ta.value = p.answer || '';
                      ta.dispatchEvent(new Event('input', { bubbles: true }));
                      ta.dispatchEvent(new Event('change', { bubbles: true }));
                      ok = ta.value.trim().length > 0;
                      got = '已填 ' + ta.value.length + ' 字';
                    }
                  }
                  out.push({ qid: p.qid, no: p.no, ok: ok, got: got, why: ok ? '' : '答题编辑器未就绪' });
                  continue;
                }
                // 模板 A：多个独立空位 answerEditor<qid><n>；只有 1 个空时整段写入，不按 | 拆分
                const single = (p.blanks || 1) <= 1;
                const parts = single
                  ? [p.answer || '']
                  : (p.answer || '').split(/[|｜]/).map((s) => s.trim());
                let filled = 0;
                let missed = 0;
                const n = Math.max(p.blanks || 0, parts.length);
                for (let i = 1; i <= n; i++) {
                  const id = 'answerEditor' + p.qid + i;
                  if (document.getElementById(id)) {
                    const ed = await waitEditor(id);
                    try {
                      if (!ed) throw new Error('编辑器未就绪');
                      ed.setContent('<p>' + esc(parts[i - 1] || '') + '</p>');
                      if ((ed.getContent() || '').replace(/<[^>]*>/g, '').trim().length > 0) filled++;
                      else missed++;
                    } catch (e) { missed++; }
                    continue;
                  }
                  const inputs = [...el.querySelectorAll('input[type=text], input:not([type]), textarea')].filter((x) => x.id !== ('answer' + p.qid));
                  if (inputs[i - 1] && parts[i - 1]) {
                    inputs[i - 1].value = parts[i - 1];
                    inputs[i - 1].dispatchEvent(new Event('input', { bubbles: true }));
                    inputs[i - 1].dispatchEvent(new Event('change', { bubbles: true }));
                    filled++;
                  } else missed++;
                }
                out.push({
                  qid: p.qid, no: p.no,
                  ok: missed === 0 && filled > 0,
                  got: '已填 ' + filled + '/' + n + ' 空' + (missed > 0 ? '（' + missed + ' 空未写入）' : ''),
                  why: missed === 0 ? '' : '答题编辑器未就绪',
                });
                continue;
              }
              if (p.type === 'short') {
                const ed = await waitEditor('answer' + p.qid);
                if (ed) {
                  ed.setContent('<p>' + esc(p.answer) + '</p>');
                  const txt = (ed.getContent() || '').replace(/<[^>]*>/g, '').trim();
                  out.push({ qid: p.qid, no: p.no, ok: txt.length > 0, got: '已填 ' + txt.length + ' 字' });
                } else {
                  const ta = el.querySelector('#answer' + p.qid);
                  if (ta) {
                    ta.value = p.answer;
                    ta.dispatchEvent(new Event('input', { bubbles: true }));
                    ta.dispatchEvent(new Event('change', { bubbles: true }));
                    out.push({ qid: p.qid, no: p.no, ok: p.answer.trim().length > 0, got: '已填 ' + p.answer.length + ' 字' });
                  } else {
                    out.push({ qid: p.qid, no: p.no, ok: false, why: '未找到答题输入框' });
                  }
                }
                continue;
              }
              out.push({ qid: p.qid, no: p.no, ok: false, why: '不支持的题型' });
            } catch (e) {
              out.push({ qid: p.qid, no: p.no, ok: false, why: '写入异常：' + (e && e.message ? e.message : e) });
            }
          }
          return out;
        }
        """;

    // ---------- 提示词与答案解析 ----------

    /// <summary>一次分批作答的汇总结果：答案按题目在 ask 中的序号（1 起）索引。</summary>
    internal sealed record AiAnswerSet(string Model, Dictionary<int, string> Answers, int PromptTokens, int CompletionTokens);

    /// <summary>AI帮做阶段提示；Batch/Batches 为当前批次，EtaSeconds=0 表示尚无法估算。</summary>
    internal sealed record AiProgress(string Text, int Batch = 0, int Batches = 0, int EtaSeconds = 0);

    /// <summary>题量超过该值且开启思考链时，先让用户确认 token 消耗。</summary>
    internal const int LargeSolveThreshold = 200;

    /// <summary>
    /// 分批向模型请求答案。思考模式会把思维链和答案一起计入 max_tokens，
    /// 一次请求装不下太多题（实测 5 道计算题思考约 1.4 万 token），
    /// 因此按题型估算分批；任一批仍被截断时二分重试。
    /// </summary>
    internal static async Task<AiAnswerSet> AskAiAsync(List<JsonObject> ask, string? systemOverride,
        Action<AiProgress>? progress, Func<int, Task<bool>>? confirmLarge, CancellationToken ct)
    {
        var thinking = AiService.ThinkingActive();
        if (thinking && ask.Count > LargeSolveThreshold && confirmLarge != null)
        {
            if (!await confirmLarge(ask.Count))
                throw new CoreException("AI_CANCELLED", "已终止本次 AI 作答", false);
        }

        var batches = PlanBatches(ask);
        var answers = new Dictionary<int, string>();
        var model = "";
        var promptTokens = 0;
        var completionTokens = 0;
        var started = Stopwatch.StartNew();

        for (var i = 0; i < batches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            // 用已完成批次的平均耗时估算剩余批次；首批按每批约 1 分钟粗估，之后用实测平均修正
            var eta = i > 0
                ? (int)Math.Round(started.Elapsed.TotalSeconds / i * (batches.Count - i))
                : batches.Count * 60;
            progress?.Invoke(new AiProgress(
                batches.Count > 1
                    ? "正在请求 AI 作答（第 " + (i + 1) + "/" + batches.Count + " 批）…"
                    : "正在请求 AI 作答…",
                i + 1, batches.Count, eta));

            var r = await AskBatchAsync(batches[i], ask, systemOverride, thinking, ct);
            if (r.Model.Length > 0) model = r.Model;
            promptTokens += r.PromptTokens;
            completionTokens += r.CompletionTokens;
            foreach (var kv in r.Answers) answers[kv.Key] = kv.Value;
        }

        return new AiAnswerSet(model, answers, promptTokens, completionTokens);
    }

    /// <summary>按「答案预算 4096」把题目切成批次，单批不超过该预算。</summary>
    static List<List<int>> PlanBatches(List<JsonObject> ask)
    {
        const int baseCost = 400;
        const int budget = 4096;
        var batches = new List<List<int>>();
        var current = new List<int>();
        var cost = baseCost;
        for (var i = 0; i < ask.Count; i++)
        {
            var c = AnswerCost(ask[i]);
            if (current.Count > 0 && cost + c > budget)
            {
                batches.Add(current);
                current = new List<int>();
                cost = baseCost;
            }
            current.Add(i);
            cost += c;
        }
        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    /// <summary>与 BuildPrompt 的预算口径保持一致：选择/判断少，填空按空数，简答最多。</summary>
    static int AnswerCost(JsonObject q)
    {
        var type = q["type"]?.GetValue<string>() ?? "";
        if (type is "single" or "multi" or "judge") return 12;
        if (type == "fill") return 30 * Math.Max(1, q["blanks"]?.GetValue<int>() ?? 1);
        return 900;
    }

    static async Task<AiAnswerSet> AskBatchAsync(List<int> batch, List<JsonObject> ask,
        string? systemOverride, bool thinking, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var questions = batch.Select(i => ask[i]).ToList();
        try
        {
            var (systemPrompt, userPrompt, maxTokens) = BuildPrompt(questions, systemOverride, thinking);
            var (model, text, promptTokens, completionTokens) =
                await AiService.ChatAsync(systemPrompt, userPrompt, maxTokens, ct);

            var parsed = ParseAnswers(text, questions.Count);
            var map = new Dictionary<int, string>();
            for (var k = 1; k <= questions.Count; k++)
                if (parsed.TryGetValue(k, out var a) && a.Length > 0)
                    map[batch[k - 1] + 1] = a;
            return new AiAnswerSet(model, map, promptTokens, completionTokens);
        }
        catch (CoreException e) when (e.Code == "AI_TRUNCATED" && batch.Count > 1)
        {
            ct.ThrowIfCancellationRequested();
            var half = batch.Count / 2;
            var first = await AskBatchAsync(batch.Take(half).ToList(), ask, systemOverride, thinking, ct);
            var second = await AskBatchAsync(batch.Skip(half).ToList(), ask, systemOverride, thinking, ct);

            var map = new Dictionary<int, string>();
            foreach (var kv in first.Answers) map[kv.Key] = kv.Value;
            foreach (var kv in second.Answers) map[kv.Key] = kv.Value;
            return new AiAnswerSet(
                second.Model.Length > 0 ? second.Model : first.Model, map,
                first.PromptTokens + second.PromptTokens,
                first.CompletionTokens + second.CompletionTokens);
        }
    }

    /// <summary>
    /// 控制 token：题干最多 300 字、每选项最多 120 字，只带必要上下文；
    /// 所有题目合成一次请求，输出格式固定为「题号|答案」一行一题。
    /// thinking=true 时思维链也计入 max_tokens，需额外预留（否则答案还没开始输出就被截断）。
    /// </summary>
    internal static (string System, string User, int MaxTokens) BuildPrompt(List<JsonObject> questions, string? systemOverride = null, bool thinking = false)
    {
        var sb = new StringBuilder();
        var maxTokens = 400;   // 推理型模型会先产生思考 token，预留余量（max_tokens 只是上限，不强制消耗）
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            var type = q["type"]?.GetValue<string>() ?? "";
            var stem = q["stem"]?.GetValue<string>() ?? "";
            if (i > 0) sb.Append('\n');
            sb.Append(i + 1);
            if (type is "single" or "multi")
            {
                sb.Append(type == "single" ? "[单] " : "[多] ").Append(stem).Append(" || ");
                var options = q["options"] as JsonArray ?? new JsonArray();
                for (var j = 0; j < options.Count; j++)
                {
                    if (j > 0) sb.Append(" | ");
                    var o = options[j];
                    sb.Append(o?["letter"]?.GetValue<string>() ?? "").Append('.').Append(o?["text"]?.GetValue<string>() ?? "");
                }
                maxTokens += 12;
            }
            else if (type == "judge")
            {
                sb.Append("[判] ").Append(stem);
                maxTokens += 12;
            }
            else if (type == "fill")
            {
                var blanks = Math.Max(1, q["blanks"]?.GetValue<int>() ?? 1);
                sb.Append("[填").Append(blanks).Append("] ").Append(stem);
                maxTokens += 30 * blanks;
            }
            else
            {
                sb.Append("[简] ").Append(stem);
                maxTokens += 900;   // 简答：默认尽量控制在300字内，内容多时可到500字，题目另有要求可再放宽
            }
        }

        const string assignmentSystem =
            "你是作业作答器。只输出答案，不要解释、不要重复题目、不要输出无关文字。\n" +
            "每题一行，格式：题号|答案。\n" +
            "单选/多选只写选项字母；判断题只写“对”或“错”；填空题按空顺序用|分隔。\n" +
            "简答题只写一段话：尽量不超过300字；若题目内容确实需要更多，可放宽到500字以内；" +
            "仅当题目明确要求字数（如“不少于800字”“详细论述”）时，才按要求作答。";

        // DeepSeek 思考模式下思维链与答案共用 max_tokens，需留足推理空间（实测 5 道计算题思考约 1.4 万）
        if (thinking) maxTokens += 20000;
        maxTokens = thinking
            ? Math.Clamp(maxTokens, 8192, 32768)
            : Math.Clamp(maxTokens, 768, 4096);
        return (systemOverride ?? assignmentSystem, sb.ToString(), maxTokens);
    }

    /// <summary>
    /// 选择题答案归一化：模型常把选项文字一起输出（如「A.代理模式」），
    /// 只取开头连续的大写字母/分隔符，避免把选项文字里的英文误当答案。
    /// </summary>
    internal static string NormalizeAnswer(string answer, string type)
    {
        if (type != "single" && type != "multi") return answer;

        // 允许「ABCDE」「A,B」「A、B」三种写法；遇到首个非字母（如「A.选项」）即停止
        var m = Regex.Match(answer, @"^\s*([A-Za-z](?:\s*[,，、/&]?\s*[A-Za-z])*)");
        if (m.Success)
            return string.Concat(m.Groups[1].Value.Where(char.IsLetter)).ToUpperInvariant();

        // 兜底：整段只有少量英文字母（如「选项A」）时取字母
        var letters = Regex.Matches(answer, "[A-Za-z]").Select(x => char.ToUpperInvariant(x.Value[0])).ToArray();
        return letters.Length is > 0 and <= 4 ? new string(letters) : answer;
    }

    /// <summary>解析「题号|答案」；兼容全角分隔符与代码块围栏，答案去掉首尾引号与标点。</summary>
    internal static Dictionary<int, string> ParseAnswers(string text, int count)
    {
        var dict = new Dictionary<int, string>();
        text = Regex.Replace(text, "```[a-zA-Z]*", "");

        var matches = Regex.Matches(text, @"(?m)^\s*(\d+)\s*[|｜:：]\s*(.+?)\s*$");
        if (matches.Count == 0)
            matches = Regex.Matches(text, @"(?m)^\s*(\d+)\s*[.、]\s*(.+?)\s*$");

        foreach (Match m in matches)
        {
            if (!int.TryParse(m.Groups[1].Value, out var no) || no < 1 || no > count) continue;
            var answer = m.Groups[2].Value.Trim().Trim('"', '“', '”', '\'', '。', '.', ' ');
            if (answer.Length > 0 && !dict.ContainsKey(no)) dict[no] = answer;
        }
        return dict;
    }
}
