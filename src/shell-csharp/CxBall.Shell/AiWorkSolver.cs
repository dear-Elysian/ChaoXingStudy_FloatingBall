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

    /// <summary>右键「AI帮做」主流程。progress 用于向前端推送阶段提示。</summary>
    public static async Task<JsonObject> SolveAsync(Uri url, JsonArray cookies, Action<string>? progress)
    {
        var exe = RequireChromiumBrowser();

        progress?.Invoke("正在启动作答浏览器…");
        var port = await EnsureBrowserAsync(exe);
        var wsUrl = await EnsurePageTargetAsync(port, url.AbsoluteUri);

        await using var cdp = await CdpClient.ConnectAsync(wsUrl, TimeSpan.FromSeconds(10));
        var cookieErrors = await PrepareSessionAsync(cdp, cookies);

        progress?.Invoke("正在打开作业页面…");
        await cdp.CallAsync("Page.enable", null, 15000);
        await cdp.CallAsync("Page.navigate", new JsonObject { ["url"] = url.AbsoluteUri }, 20000);

        progress?.Invoke("正在等待题目加载…");
        var page = await WaitForQuestionsAsync(cdp, 45000);
        if (page["login"]?.GetValue<bool>() == true)
            throw new CoreException("SESSION_EXPIRED", "学习通登录态已失效，请先在应用内重新登录", false);
        if ((page["n"]?.GetValue<int>() ?? 0) == 0)
            throw new CoreException("AI_PAGE_TIMEOUT",
                cookieErrors > 0
                    ? "作业页面未加载出题目，且部分登录 Cookie 写入失败，请重新登录后再试"
                    : "作业页面加载超时或未找到题目，请确认该作业仍可作答", false);

        progress?.Invoke("正在读取题目…");
        var extracted = await cdp.EvaluateAsync(ExtractJs, 20000) as JsonArray ?? new JsonArray();
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

        progress?.Invoke("正在请求 AI 作答…");
        var (systemPrompt, userPrompt, maxTokens) = BuildPrompt(ask);
        var (model, text, promptTokens, completionTokens) =
            await AiService.ChatAsync(systemPrompt, userPrompt, maxTokens, CancellationToken.None);

        var answers = ParseAnswers(text, ask.Count);
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

        progress?.Invoke("正在填写答案…");
        var applied = plan.Count > 0
            ? await cdp.EvaluateAsync("(" + ApplyJs + ")(" + plan.ToJsonString() + ")", 30000) as JsonArray ?? new JsonArray()
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
    internal static async Task<int> PrepareSessionAsync(CdpClient cdp, JsonArray cookies)
    {
        await cdp.CallAsync("Network.enable", null, 15000);
        try { await cdp.CallAsync("Network.clearBrowserCookies", null, 15000); } catch (CoreException) { }

        var errors = 0;
        foreach (var c in cookies)
        {
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
    internal static async Task<int> EnsureBrowserAsync(string exe)
    {
        var marker = LoadMarker(exe);
        if (marker != null && await IsPortAlive(marker.Value.Port))
            return marker.Value.Port;

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
        psi.ArgumentList.Add("about:blank");

        try { Process.Start(psi); }
        catch (Exception ex)
        {
            throw new CoreException("AI_BROWSER_FAILED", "无法启动浏览器：" + ex.Message, false);
        }

        for (var i = 0; i < 80; i++)
        {
            if (await IsPortAlive(port))
            {
                SaveMarker(exe, port);
                return port;
            }
            await Task.Delay(250);
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

    /// <summary>找到一个可导航的 page target；优先复用已打开同一页面的标签页。</summary>
    internal static async Task<string> EnsurePageTargetAsync(int port, string pageUrl)
    {
        var list = await ReadTargetsAsync(port);
        var same = list.FirstOrDefault(t => (t["url"]?.GetValue<string>() ?? "").StartsWith(pageUrl, StringComparison.OrdinalIgnoreCase));
        if (same?["webSocketDebuggerUrl"]?.GetValue<string>() is string sameUrl && sameUrl.Length > 0) return sameUrl;

        var blank = list.FirstOrDefault(t => (t["url"]?.GetValue<string>() ?? "") is "about:blank" or "");
        if (blank?["webSocketDebuggerUrl"]?.GetValue<string>() is string blankUrl && blankUrl.Length > 0) return blankUrl;

        try
        {
            using var resp = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Put, "http://127.0.0.1:" + port + "/json/new?about:blank"));
            if (resp.IsSuccessStatusCode
                && JsonNode.Parse(await resp.Content.ReadAsStringAsync()) is JsonObject created
                && created["webSocketDebuggerUrl"]?.GetValue<string>() is string createdUrl && createdUrl.Length > 0)
                return createdUrl;
        }
        catch { }

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
    static async Task<JsonObject> WaitForQuestionsAsync(CdpClient cdp, int timeoutMs)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        JsonObject last = new();
        while (DateTimeOffset.Now < deadline)
        {
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
            await Task.Delay(400);
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
        (plan) => {
          const out = [];
          const letterOf = (o) => { const n = o.querySelector('.num_option, .num_option_dx'); return n ? (n.getAttribute('data') || '') : ''; };
          const selected = (o) => !!(o.querySelector('.num_option.check_answer, .num_option_dx.check_answer_dx'));
          const pick = (opts, letter) => opts.find((o) => letterOf(o).toUpperCase() === letter);
          const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/\n/g, '<br>');
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
                const parts = (p.answer || '').split(/[|｜]/).map((s) => s.trim());
                let filled = 0;
                const n = Math.max(p.blanks || 0, parts.length);
                for (let i = 1; i <= n; i++) {
                  const id = 'answerEditor' + p.qid + i;
                  const ed = window.UE && UE.getEditor ? UE.getEditor(id) : null;
                  if (ed) {
                    ed.setContent('<p>' + esc(parts[i - 1] || '') + '</p>');
                    if ((ed.getContent() || '').replace(/<[^>]*>/g, '').trim().length > 0) filled++;
                    continue;
                  }
                  const inputs = [...el.querySelectorAll('input[type=text], input:not([type]), textarea')].filter((x) => x.id !== ('answer' + p.qid));
                  if (inputs[i - 1] && parts[i - 1]) {
                    inputs[i - 1].value = parts[i - 1];
                    inputs[i - 1].dispatchEvent(new Event('input', { bubbles: true }));
                    inputs[i - 1].dispatchEvent(new Event('change', { bubbles: true }));
                    filled++;
                  }
                }
                out.push({ qid: p.qid, no: p.no, ok: filled > 0 && filled >= (p.blanks || 1), got: '已填 ' + filled + '/' + (p.blanks || n) + ' 空' });
                continue;
              }
              if (p.type === 'short') {
                const ed = window.UE && UE.getEditor ? UE.getEditor('answer' + p.qid) : null;
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

    /// <summary>
    /// 控制 token：题干最多 300 字、每选项最多 120 字，只带必要上下文；
    /// 所有题目合成一次请求，输出格式固定为「题号|答案」一行一题。
    /// </summary>
    internal static (string System, string User, int MaxTokens) BuildPrompt(List<JsonObject> questions, string? systemOverride = null)
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

        maxTokens = Math.Clamp(maxTokens, 768, 4096);
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
