using System.Text.Json.Nodes;

namespace CxBall.Shell;

/// <summary>
/// 随堂练习 AI帮做（§7.9）：用外部浏览器打开练习作答页，从页面 Vue 数据读取题目 →
/// 一次批量请求模型 → 把答案写回页面表单。不自动提交，由用户检查后手动提交。
///
/// 只处理单选/多选/判断/填空/简答（简答仅限文字作答的 4/5/6/7 型）；
/// 含图片、附件、链接或要求画图/上传图片的题目整题跳过；口语题、分录题、资料题不处理。
/// </summary>
static class AiQuizSolver
{
    /// <summary>右键「AI帮做」主流程（随堂练习）。progress 用于向前端推送阶段提示。</summary>
    public static async Task<JsonObject> SolveAsync(Uri url, JsonArray cookies, Action<string>? progress)
    {
        var exe = AiWorkSolver.RequireChromiumBrowser();

        progress?.Invoke("正在启动作答浏览器…");
        var port = await AiWorkSolver.EnsureBrowserAsync(exe);
        var wsUrl = await AiWorkSolver.EnsurePageTargetAsync(port, url.AbsoluteUri);

        await using var cdp = await CdpClient.ConnectAsync(wsUrl, TimeSpan.FromSeconds(10));
        var cookieErrors = await AiWorkSolver.PrepareSessionAsync(cdp, cookies);

        progress?.Invoke("正在打开练习页面…");
        await cdp.CallAsync("Page.enable", null, 15000);
        await cdp.CallAsync("Page.navigate", new JsonObject { ["url"] = url.AbsoluteUri }, 20000);

        progress?.Invoke("正在等待题目加载…");
        var page = await WaitForQuizAsync(cdp, 45000);
        var href = page["url"]?.GetValue<string>() ?? "";
        if (page["login"]?.GetValue<bool>() == true || href.Contains("passport2", StringComparison.OrdinalIgnoreCase))
            throw new CoreException("SESSION_EXPIRED", "学习通登录态已失效，请先在应用内重新登录后再使用 AI帮做", false);
        if (href.Contains("quizStudentQuestion", StringComparison.OrdinalIgnoreCase)
            || href.Contains("quizStatisticsPage", StringComparison.OrdinalIgnoreCase))
            throw new CoreException("AI_QUIZ_CLOSED", "该练习已提交或已结束，无法帮做", false);

        var status = page["status"]?.GetValue<int>();
        if (status is not null && status != 1)
            throw new CoreException(status == 0 ? "AI_QUIZ_NOT_STARTED" : "AI_QUIZ_CLOSED",
                status == 0 ? "该练习尚未开始，无法帮做" : "该练习已结束，无法帮做", false);
        if (page["permission"]?.GetValue<int>() == 0)
            throw new CoreException("AI_QUIZ_NO_PERMISSION", "你没有该练习的作答权限，无法帮做", false);
        if ((page["n"]?.GetValue<int>() ?? 0) == 0)
            throw new CoreException("AI_PAGE_TIMEOUT",
                cookieErrors > 0
                    ? "练习页面未加载出题目，且部分登录 Cookie 写入失败，请重新登录后再试"
                    : "练习页面加载超时或未找到题目，请确认该练习仍可作答", false);

        progress?.Invoke("正在读取题目…");
        var extracted = await cdp.EvaluateAsync(ExtractJs, 20000) as JsonArray ?? new JsonArray();
        var all = extracted.OfType<JsonObject>().ToList();
        if (all.Count == 0)
            throw new CoreException("AI_NO_QUESTIONS", "该练习没有可作答的题目", false);

        // 题材筛选全部由程序完成，跳过的题目不送模型，节省 token
        var ask = new List<JsonObject>();
        var notes = new List<string>();
        foreach (var q in all)
        {
            var type = q["type"]?.GetValue<string>() ?? "";
            var no = q["no"]?.GetValue<string>() ?? "";
            var tag = no.Length > 0 ? "第" + no + "题" : "该题";
            if (type == "oral")
            {
                notes.Add(tag + "（口语题）需要录音，已跳过");
                continue;
            }
            if (type == "special")
            {
                notes.Add(tag + "（分录题/资料题）不在支持范围，已跳过");
                continue;
            }
            if (type.Length == 0 || type == "other")
            {
                notes.Add(tag + "题型无法识别，已跳过");
                continue;
            }
            if ((q["attach"]?.GetValue<int>() ?? 0) > 0 || (q["media"]?.GetValue<int>() ?? 0) > 0)
            {
                notes.Add(tag + "含图片或附件，已跳过");
                continue;
            }
            if (type == "short" && (q["link"]?.GetValue<int>() ?? 0) > 0)
            {
                notes.Add(tag + "（简答题）含文件或链接，已跳过");
                continue;
            }
            if (type == "short" && (q["draw"]?.GetValue<int>() ?? 0) > 0)
            {
                notes.Add(tag + "（简答题）要求画图或上传图片，已跳过");
                continue;
            }
            if (type == "fill" && (q["blanks"]?.GetValue<int>() ?? 0) == 0)
            {
                notes.Add(tag + "（填空题）未识别到空位，已跳过");
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
            throw new CoreException("AI_NO_QUESTIONS",
                "没有找到可交给 AI 作答的题目（仅支持选择/判断/填空/简答；含图片、附件或需要画图的题目会跳过）", false);

        progress?.Invoke("正在请求 AI 作答…");
        var (systemPrompt, userPrompt, maxTokens) = AiWorkSolver.BuildPrompt(ask, QuizSystemPrompt);
        var (model, text, promptTokens, completionTokens) =
            await AiService.ChatAsync(systemPrompt, userPrompt, maxTokens, CancellationToken.None);

        var answers = AiWorkSolver.ParseAnswers(text, ask.Count);
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
            var type = q["type"]?.GetValue<string>() ?? "";
            plan.Add(new JsonObject
            {
                ["qid"] = q["qid"]?.GetValue<string>() ?? "",
                ["no"] = q["no"]?.GetValue<string>() ?? "",
                ["type"] = type,
                ["answer"] = AiWorkSolver.NormalizeAnswer(answer, type),
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
        if (all.Count > ask.Count) message += "，跳过 " + (all.Count - ask.Count) + " 题";
        if (failed > 0) message += "，失败 " + failed + " 题";
        message += "。请在浏览器中检查答案后手动提交。";

        return new JsonObject
        {
            ["kind"] = "practice",
            ["model"] = model,
            ["total"] = all.Count,
            ["writable"] = all.Count,
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

    // ---------- 页面脚本 ----------

    /// <summary>等待随堂练习页的 Vue 实例完成题目加载；跳登录页/结果页/不可作答时提前返回。</summary>
    static async Task<JsonObject> WaitForQuizAsync(CdpClient cdp, int timeoutMs)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        JsonObject last = new();
        while (DateTimeOffset.Now < deadline)
        {
            try
            {
                var v = await cdp.EvaluateAsync("""
                    (() => {
                      const vm = window.vm;
                      const href = location.href || '';
                      const num = (x) => {
                        const n = Number(x);
                        return Number.isFinite(n) ? n : null;
                      };
                      return {
                        n: vm && Array.isArray(vm.questionList) ? vm.questionList.length : 0,
                        status: vm && vm.active ? num(vm.active.status) : null,
                        permission: vm ? num(vm.hasAnswerPermission) : null,
                        login: /passport2|passport\.chaoxing/i.test(href)
                          || !!document.querySelector('input[name="uname"], #loginForm, .login-box'),
                        url: href,
                        title: document.title
                      };
                    })()
                    """, 10000);
                if (v is JsonObject o) last = o;
                if (last["login"]?.GetValue<bool>() == true) return last;
                var href = last["url"]?.GetValue<string>() ?? "";
                if (href.Contains("quizStudentQuestion", StringComparison.OrdinalIgnoreCase)
                    || href.Contains("quizStatisticsPage", StringComparison.OrdinalIgnoreCase)) return last;
                var status = last["status"]?.GetValue<int>();
                if (status is not null && status != 1) return last;
                if (last["permission"]?.GetValue<int>() == 0) return last;
                if ((last["n"]?.GetValue<int>() ?? 0) > 0) return last;
            }
            catch (CoreException) { /* 导航过程中执行上下文会被销毁，继续重试 */ }
            await Task.Delay(400);
        }
        return last;
    }

    /// <summary>
    /// 只读提取：题号/题型/题干/选项/空数/附件与媒体/是否含链接/是否要求画图。
    /// 题干与选项截断以控制 token；题型按页面模板编码（0 单选 / 1 多选 / 3、16 判断 / 2 填空 / 4、5、6、7 简答）。
    /// </summary>
    const string ExtractJs = """
        (() => {
          const vm = window.vm;
          if (!vm || !Array.isArray(vm.questionList)) return [];
          const toText = (html) => {
            const d = document.createElement('div');
            d.innerHTML = String(html || '');
            return (d.innerText || d.textContent || '').replace(/\s+/g, ' ').trim();
          };
          const TYPE = { 0:'single', 1:'multi', 2:'fill', 3:'judge', 4:'short', 5:'short', 6:'short', 7:'short', 16:'judge', 9:'special', 10:'special', 18:'oral' };
          return vm.questionList.map((q, i) => {
            const rawStem = toText(q.content);
            let stem = rawStem.length > 300 ? rawStem.slice(0, 300) + '…' : rawStem;
            const options = (Array.isArray(q.answer) ? q.answer : []).map((o) => {
              let t = toText(o && o.content);
              if (t.length > 120) t = t.slice(0, 120) + '…';
              return { letter: String((o && o.name) || ''), text: t };
            });
            const blanks = Array.isArray(q.answer) ? q.answer.length : 0;
            const optionHtml = (Array.isArray(q.answer) ? q.answer : []).map((o) => String((o && o.content) || '')).join('');
            const optionRecs = (Array.isArray(q.answer) ? q.answer : []).reduce((n, o) =>
              n + (Array.isArray(o && o.recs) ? o.recs.length : 0), 0);
            const attach = (Array.isArray(q.recArray) ? q.recArray.length : 0)
                         + (Array.isArray(q.imgArray) ? q.imgArray.length : 0)
                         + optionRecs;
            const html = String(q.content || '') + optionHtml;
            return {
              qid: String(q.id || ''),
              no: String(q.sequence || (i + 1)),
              type: TYPE[q.type] || 'other',
              rawType: q.type,
              stem: stem,
              options: options,
              blanks: blanks,
              attach: attach,
              media: /<img|<video|<audio|<iframe|<object|<embed/i.test(html) ? 1 : 0,
              link: /<a\s[^>]*href/i.test(html) ? 1 : 0,
              draw: /画出|画图|绘制|绘图|作图|手绘|描绘|画一下|画一个|请画|上传图片|上传照片|上传附件|拍照|截图/.test(rawStem) ? 1 : 0,
              editable: true
            };
          });
        })()
        """;

    /// <summary>
    /// 写回答案：直接修改页面 Vue 数据（与手动点击/输入同一份状态），
    /// 选择/判断写 myoption，填空写 blankAnswer，简答写 content；全部保留原有字段并在下次渲染时生效。
    /// </summary>
    const string ApplyJs = """
        (plan) => {
          const out = [];
          const vm = window.vm;
          if (!vm || !Array.isArray(vm.questionList)) {
            return plan.map((p) => ({ qid: p.qid, no: p.no, ok: false, why: '页面数据已失效，请重新打开练习页' }));
          }
          const textOf = (html) => {
            const d = document.createElement('div');
            d.innerHTML = String(html || '');
            return (d.textContent || '').replace(/\s+/g, '').trim();
          };
          for (const p of plan) {
            const q = vm.questionList.find((x) => String(x.id) === String(p.qid));
            if (!q) { out.push({ qid: p.qid, no: p.no, ok: false, why: '题目已失效，请刷新练习页' }); continue; }
            try {
              const opts = Array.isArray(q.answer) ? q.answer : [];
              if (p.type === 'single' || p.type === 'multi') {
                const lead = (p.answer || '').match(/^([A-Za-z](?:\s*[,，、/&]?\s*[A-Za-z])*)/);
                let letters = lead
                  ? [...new Set(lead[1].replace(/[^A-Za-z]/g, '').toUpperCase().split(''))]
                  : [];
                if (p.type === 'single') letters = letters.slice(0, 1);
                if (letters.length === 0) {
                  out.push({ qid: p.qid, no: p.no, ok: false, why: 'AI 未给出选项字母' });
                  continue;
                }
                const names = [];
                for (const l of letters) {
                  const o = opts.find((x) => String(x.name || '').toUpperCase() === l);
                  if (o) names.push(String(o.name));
                }
                if (names.length !== letters.length) {
                  out.push({ qid: p.qid, no: p.no, ok: false, why: '未找到对应选项' });
                  continue;
                }
                let my = p.type === 'single'
                  ? names[0]
                  : (names.every((n) => /^[a-zA-Z]$/.test(n)) ? names.slice().sort().join('') : names.join(','));
                q.personAnswer = Object.assign({}, q.personAnswer || {}, { myoption: my });
                out.push({ qid: p.qid, no: p.no, ok: true, got: my });
                continue;
              }
              if (p.type === 'judge') {
                const want = /对|正确|true|√|是/i.test(p.answer) ? 'true'
                           : /错|错误|false|×|否/i.test(p.answer) ? 'false' : '';
                let target = null;
                if (want) {
                  target = opts.find((o) => want === 'true'
                    ? /对|正确|√|是|true/i.test(textOf(o.content))
                    : /错|错误|×|否|false/i.test(textOf(o.content)));
                }
                if (!target) {
                  const l = (String(p.answer || '').match(/[A-Za-z]/) || [''])[0].toUpperCase();
                  if (l) target = opts.find((o) => String(o.name || '').toUpperCase() === l);
                }
                if (!target) {
                  out.push({ qid: p.qid, no: p.no, ok: false, why: '未找到对应判断选项' });
                  continue;
                }
                q.personAnswer = Object.assign({}, q.personAnswer || {}, { myoption: String(target.name) });
                out.push({ qid: p.qid, no: p.no, ok: true, got: String(target.content || '') });
                continue;
              }
              if (p.type === 'fill') {
                const parts = (p.answer || '').split(/[|｜]/).map((s) => s.trim());
                const base = opts;
                const n = Math.max(p.blanks || 0, base.length, parts.length);
                const arr = [];
                for (let i = 0; i < n; i++) {
                  arr.push({
                    name: String((base[i] && base[i].name) || String.fromCharCode(65 + i)),
                    content: parts[i] || ''
                  });
                }
                q.personAnswer = Object.assign({}, q.personAnswer || {}, { blankAnswer: arr });
                const done = arr.filter((x) => (x.content || '').trim().length > 0).length;
                out.push({ qid: p.qid, no: p.no, ok: done > 0, got: '已填 ' + done + '/' + n + ' 空', why: done > 0 ? '' : '未写入答案' });
                continue;
              }
              if (p.type === 'short') {
                const content = String(p.answer || '').trim();
                q.personAnswer = Object.assign({}, q.personAnswer || {}, { content: content });
                out.push({ qid: p.qid, no: p.no, ok: content.length > 0, got: '已填 ' + content.length + ' 字', why: content.length > 0 ? '' : '未写入答案' });
                continue;
              }
              out.push({ qid: p.qid, no: p.no, ok: false, why: '不支持的题型' });
            } catch (e) {
              out.push({ qid: p.qid, no: p.no, ok: false, why: '写入异常：' + (e && e.message ? e.message : e) });
            }
          }
          if (vm.$forceUpdate) vm.$forceUpdate();
          return out;
        }
        """;

    /// <summary>随堂练习专用系统提示词：题型与作答范围不同，简答字数上限与作业一致。</summary>
    const string QuizSystemPrompt =
        "你是随堂练习作答器。只输出答案，不要解释、不要重复题目、不要输出无关文字。\n" +
        "每题一行，格式：题号|答案。\n" +
        "单选只写一个选项字母；多选只写选项字母（如ABC）；判断题只写“对”或“错”；填空题按空顺序用|分隔。\n" +
        "简答题只写一段话：尽量不超过300字；若题目内容确实需要更多，可放宽到500字以内；" +
        "仅当题目明确要求字数（如“不少于800字”“详细论述”）时，才按要求作答。";
}
