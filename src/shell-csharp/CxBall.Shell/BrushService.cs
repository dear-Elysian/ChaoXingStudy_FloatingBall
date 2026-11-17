using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace CxBall.Shell;

/// <summary>刷课进度（v3.28）：课程名 + 当前章节，index/total 为章节序号。</summary>
public sealed record BrushProgress(string CourseName, string ChapterLabel, int Index, int Total);

/// <summary>
/// 刷课执行器（v3.28）：用设置里的外部 Chromium 浏览器打开章节学习页，
/// 逐章处理文档 / 视频 / 音频任务点；做题（work）、外链等一律跳过。
///
/// 只操作页面上已有的任务点：已完成的模块先探测再跳过；文档调用 finishJob，
/// 视频/音频用 videojs 播放器播放（允许倍速时 2x、允许快进时跳到 95%）。
/// </summary>
static class BrushService
{
    static readonly HashSet<string> BrushableKinds = new(StringComparer.Ordinal) { "doc", "video", "audio" };

    public static async Task<JsonObject> RunAsync(JsonObject plan, JsonArray cookies,
        Action<BrushProgress> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var courseName = plan["courseName"]?.GetValue<string>() ?? "";
        var courseId = plan["courseId"]?.GetValue<string>() ?? "";
        var classId = plan["classId"]?.GetValue<string>() ?? "";
        var cpi = plan["cpi"]?.GetValue<string>() ?? "";
        var enc = plan["enc"]?.GetValue<string>() ?? "";
        var courseEnc = plan["courseEnc"]?.GetValue<string>() ?? enc;
        var chapters = plan["chapters"] as JsonArray ?? new JsonArray();

        var exe = AiWorkSolver.RequireChromiumBrowser();
        Dbg("require browser ok: " + exe);
        var port = await AiWorkSolver.EnsureBrowserAsync(exe, ct);
        Dbg("browser port: " + port);

        var firstId = chapters.Count > 0 ? chapters[0]?["chapterId"]?.GetValue<string>() ?? "" : "";
        var firstUrl = StudyUrl(courseId, classId, cpi, enc, firstId);
        // 每次刷课新开一个标签页，避免复用上次遗留的卡死页面
        var wsUrl = await AiWorkSolver.CreatePageTargetAsync(port)
                    ?? await AiWorkSolver.EnsurePageTargetAsync(port, firstUrl, ct);
        Dbg("target: " + wsUrl);

        await using var cdp = await CdpClient.ConnectAsync(wsUrl, TimeSpan.FromSeconds(10));
        Dbg("cdp connected");
        await AiWorkSolver.PrepareSessionAsync(cdp, cookies, ct);
        ct.ThrowIfCancellationRequested();
        Dbg("session prepared");
        await cdp.CallAsync("Page.enable", null, 15000);
        Dbg("page enabled");
        // 页面级静音钩子：在页面脚本前注入，任何 play() 都先静音，避免刚进章节时短暂出声
        try
        {
            await cdp.CallAsync("Page.addScriptToEvaluateOnNewDocument",
                new JsonObject { ["source"] = MuteHookJs }, 8000);
        }
        catch (CoreException) { }

        var total = chapters.Count;
        var brushed = 0;
        var brushedChapterIds = new List<string>();
        var processedTasks = 0;
        var unfinished = false;
        var unknown = false;
        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (chapters[i] is not JsonObject chapter) continue;
            var chapterId = chapter["chapterId"]?.GetValue<string>() ?? "";
            if (chapterId.Length == 0) continue;

            progress(new BrushProgress(courseName, ChapterLabel(chapter), i + 1, total));

            var url = StudyUrl(courseId, classId, cpi, enc, chapterId);
            Dbg("chapter " + chapterId + " navigate");
            try { await cdp.CallAsync("Page.navigate", new JsonObject { ["url"] = url }, 20000); }
            catch (CoreException ex) when (!BrowserGone(ex)) { }
            // 不调用 Page.bringToFront：刷课期间浏览器被最小化时保持最小化，不主动弹出来

            var page = await WaitStudyAsync(cdp, ct);
            Dbg("chapter " + chapterId + " ready");
            // 静音兜底：页面里任何已存在/自动开始的音视频都不出声
            await MuteAllAsync(cdp);
            var result = await ProcessChapterAsync(cdp, chapter, page, ct);
            processedTasks += result.Processed;
            unfinished |= result.Unfinished;
            unknown |= result.Unknown;
            brushed++;
            if (result.Processed > 0) brushedChapterIds.Add(chapterId);
            Dbg("chapter " + chapterId + " processed=" + result.Processed
                + " unfinished=" + result.Unfinished + " unknown=" + result.Unknown);
            // 章节完成标志由服务端异步回写：稍等再切下一章，避免刚刷完就切走导致章节未打勾
            if (result.Processed > 0) await Task.Delay(2500, ct);
        }

        // 刷完后核对目录打勾：服务端回写有延迟，未打勾的章节切到别的章节停一会儿再回目录确认
        await VerifyChapterTicksAsync(cdp, courseId, classId, cpi, courseEnc, enc, brushedChapterIds, ct);

        // 本次没有任何可执行任务，且所有计划任务都已确认完成 → 视为“已刷完”
        // （做题/外链会被跳过，混合章节里可能残留作业待完成，不能据此判定课程未刷完）
        if (processedTasks == 0 && !unfinished && !unknown)
            return new JsonObject { ["kind"] = "finished", ["courseName"] = courseName, ["chapters"] = brushed };

        return new JsonObject
        {
            ["kind"] = "done",
            ["courseName"] = courseName,
            ["chapters"] = brushed,
        };
    }

    static string ChapterLabel(JsonObject chapter)
    {
        var label = chapter["label"]?.GetValue<string>() ?? "";
        var name = chapter["name"]?.GetValue<string>() ?? "";
        if (label.Length == 0) return name;
        if (name.Length == 0) return label;
        return label + " " + name;
    }

    static string StudyUrl(string courseId, string classId, string cpi, string enc, string chapterId)
        => "https://mooc1.chaoxing.com/mycourse/studentstudy?chapterId=" + chapterId
           + "&courseId=" + courseId
           + "&clazzid=" + classId
           + "&cpi=" + cpi
           + "&enc=" + enc
           + "&mooc2=1&hidetype=0&fanyaVersion=0";

    static string CoursePageUrl(string courseId, string classId, string cpi, string courseEnc)
        => "https://mooc2-ans.chaoxing.com/mooc2-ans/mycourse/studentcourse"
           + "?courseid=" + courseId
           + "&clazzid=" + classId
           + "&cpi=" + cpi
           + "&enc=" + courseEnc
           + "&t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
           + "&pageHeader=1&v=2";

    // ---------- 章节打勾核对 ----------

    /// <summary>
    /// 模块全部刷完但章节还没打勾时，服务端可能仍在异步回写：切到别的章节停一会儿，
    /// 再回目录页看是否已打勾；最多重试 2 轮，仍不打勾只记日志，不影响本次结果。
    /// </summary>
    static async Task VerifyChapterTicksAsync(CdpClient cdp, string courseId, string classId, string cpi,
        string courseEnc, string studyEnc, List<string> chapterIds, CancellationToken ct)
    {
        if (chapterIds.Count == 0) return;
        var ticks = await ReadChapterTicksAsync(cdp, courseId, classId, cpi, courseEnc, ct);
        if (ticks == null) return;
        Dbg("chapter ticks: " + string.Join(",", ticks.Select(t => t.Key + "=" + (t.Value ? "1" : "0"))));

        foreach (var chapterId in chapterIds)
        {
            ct.ThrowIfCancellationRequested();
            if (ticks.TryGetValue(chapterId, out var ticked) && ticked) continue;

            for (var round = 0; round < 2; round++)
            {
                // 切到别的章节停一会儿，给服务端回写完成标志的时间
                var other = chapterIds.FirstOrDefault(id => !string.Equals(id, chapterId, StringComparison.Ordinal)) ?? chapterId;
                Dbg("chapter " + chapterId + " not ticked, switch to " + other + " round " + round);
                try
                {
                    await cdp.CallAsync("Page.navigate",
                        new JsonObject { ["url"] = StudyUrl(courseId, classId, cpi, studyEnc, other) }, 20000);
                    await Task.Delay(4000, ct);
                    await MuteAllAsync(cdp);
                }
                catch (CoreException ex) when (!BrowserGone(ex)) { }

                ticks = await ReadChapterTicksAsync(cdp, courseId, classId, cpi, courseEnc, ct);
                if (ticks == null) return;
                if (ticks.TryGetValue(chapterId, out var ok) && ok) break;
            }
        }
    }

    const string ChapterTicksProbeJs = """
        (function(){
          try{
            if(/passport2|login/i.test(location.href)||/用户登录/.test(document.title))return{login:true};
            var items=document.querySelectorAll('div.chapter_item[id^=cur]');
            if(!items.length)return{ready:false};
            function ownTask(it){
              var all=it.querySelectorAll('*');
              for(var j=0;j<all.length;j++){
                var e=all[j];
                if(!e.classList)continue;
                if(e.classList.contains('chapter_item'))return null;
                if(e.classList.contains('catalog_task'))return e;
              }
              return null;
            }
            var ticks={};
            for(var i=0;i<items.length;i++){
              var id=items[i].id.substring(3);
              if(!id)continue;
              var t=ownTask(items[i]);
              var st=t?t.querySelector('.catalog_state'):null;
              ticks[id]=!!(st&&String(st.className).indexOf('icon_yiwanc')>=0);
            }
            return{ready:true,ticks:ticks};
          }catch(e){return{ready:false,err:String(e)};}
        })()
        """;

    static async Task<Dictionary<string, bool>?> ReadChapterTicksAsync(CdpClient cdp, string courseId,
        string classId, string cpi, string enc, CancellationToken ct)
    {
        try
        {
            await cdp.CallAsync("Page.navigate",
                new JsonObject { ["url"] = CoursePageUrl(courseId, classId, cpi, enc) }, 20000);
        }
        catch (CoreException ex) when (!BrowserGone(ex)) { }

        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            JsonObject? probe = null;
            try { probe = await cdp.EvaluateAsync(ChapterTicksProbeJs, 12000) as JsonObject; }
            catch (CoreException ex) when (!BrowserGone(ex)) { }
            catch (CoreException) { throw; }

            if (probe != null)
            {
                if (probe["login"]?.GetValue<bool>() == true)
                    throw new CoreException("SESSION_EXPIRED", "学习通登录态已失效，请先在应用内重新登录", false);
                if (probe["ready"]?.GetValue<bool>() == true && probe["ticks"] is JsonObject ticks)
                {
                    var map = new Dictionary<string, bool>(StringComparer.Ordinal);
                    foreach (var kv in ticks) map[kv.Key] = kv.Value?.GetValue<bool>() == true;
                    return map;
                }
            }
            await Task.Delay(700, ct);
        }
        Dbg("read chapter ticks timeout");
        return null;
    }

    // ---------- 静音 ----------

    // 注入到每个新文档：拦截 play() 并持续把音视频元素设为静音
    const string MuteHookJs = """
        (function(){
          try{
            var proto=HTMLMediaElement.prototype;
            var origPlay=proto.play;
            proto.play=function(){
              try{this.muted=true;this.volume=0;}catch(e){}
              return origPlay.apply(this,arguments);
            };
          }catch(e){}
          try{
            var timer=0;
            function muteAll(){
              timer=0;
              try{
                var ms=document.querySelectorAll('video,audio');
                for(var i=0;i<ms.length;i++){try{ms[i].muted=true;ms[i].volume=0;}catch(e){}}
              }catch(e){}
            }
            function schedule(){if(!timer)timer=setTimeout(muteAll,300);}
            document.addEventListener('DOMContentLoaded',muteAll);
            function attach(){
              try{new MutationObserver(schedule).observe(document.documentElement,{childList:true,subtree:true});}
              catch(e){setTimeout(attach,100);}
            }
            attach();
          }catch(e){}
        })();
        """;

    const string MuteAllJs = """
        (function(){
          function mute(d){
            try{
              var ms=d.querySelectorAll('video,audio');
              for(var i=0;i<ms.length;i++){try{ms[i].muted=true;ms[i].volume=0;}catch(e){}}
            }catch(e){}
            try{
              var fs=d.querySelectorAll('iframe');
              for(var j=0;j<fs.length;j++){
                try{var cd=fs[j].contentDocument;if(cd)mute(cd);}catch(e){}
              }
            }catch(e){}
          }
          mute(document);
          return {ok:true};
        })()
        """;

    static async Task MuteAllAsync(CdpClient cdp)
    {
        try { await cdp.EvaluateAsync(MuteAllJs, 10000); } catch (CoreException) { }
    }

    // ---------- 页面等待 ----------

    const string StudyProbeJs = """
        (function(){
          try{
            if(/passport2|login/i.test(location.href)||/用户登录/.test(document.title))return{login:true};
            // 章节顶部的任务页签（视频 / 文档 / …）：每个页签对应一个独立的 cards 页
            var tabs=[];
            try{
              var lis=document.querySelectorAll('.prev_list li');
              for(var k=0;k<lis.length;k++){
                var sp=lis[k].querySelector('.spanText');
                tabs.push({i:k,text:String(sp?sp.innerText:(lis[k].innerText||'')).replace(/\s+/g,'').trim(),
                  active:lis[k].classList.contains('active')});
              }
            }catch(e){}
            var t=document.getElementById('iframe');
            if(!t||!t.contentDocument)return{ready:false,tabs:tabs};
            var d=t.contentDocument;
            var cardsUrl=String(d.URL||'');
            // 递归收集任务模块：顶层 iframe.ans-attach-online，以及模块内部再嵌套的同类 iframe
            function collect(doc,base){
              var out=[],fs=doc.querySelectorAll('iframe.ans-attach-online');
              for(var i=0;i<fs.length;i++){
                var f=fs[i],data={};
                try{data=JSON.parse(f.getAttribute('data')||'{}')||{};}catch(e){}
                var path=base.concat([i]);
                var wj='';try{wj=String(f.contentWindow.jobid||'');}catch(e){}
                var done=false;
                try{var pe=f.parentElement;done=!!(pe&&String(pe.className).indexOf('ans-job-finished')>=0);}catch(e){}
                out.push({i:i,path:path,jobid:String(data.jobid||''),wjobid:wj,done:done,src:String(f.src||''),depth:base.length});
                if(base.length<2){
                  try{
                    var cd=f.contentDocument;
                    if(cd){var sub=collect(cd,path);for(var s=0;s<sub.length;s++)out.push(sub[s]);}
                  }catch(e){}
                }
              }
              return out;
            }
            var arr=collect(d,[]);
            if(arr.length>0)return{ready:true,frames:arr,tabs:tabs,cardsUrl:cardsUrl};
            var txt=d.body?(d.body.innerText||''):'';
            if(/暂无任务点|没有任务点|暂无内容/.test(txt))return{ready:true,frames:[],tabs:tabs,cardsUrl:cardsUrl};
            return{ready:false,tabs:tabs,cardsUrl:cardsUrl};
          }catch(e){return{ready:false,err:String(e)};}
        })()
        """;

    static async Task<JsonObject> WaitStudyAsync(CdpClient cdp, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        var lastCount = -1;
        var stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            JsonObject? page = null;
            try { page = await cdp.EvaluateAsync(StudyProbeJs, 12000) as JsonObject; }
            catch (CoreException ex) when (!BrowserGone(ex)) { }
            catch (CoreException) { throw; }

            if (page != null)
            {
                if (page["login"]?.GetValue<bool>() == true)
                    throw new CoreException("SESSION_EXPIRED", "学习通登录态已失效，请先在应用内重新登录", false);
                if (page["ready"]?.GetValue<bool>() == true)
                {
                    var frames = page["frames"] as JsonArray;
                    var count = frames?.Count ?? 0;
                    if (count == 0) return page;
                    if (count == lastCount)
                    {
                        stable++;
                        if (stable >= 2) return page;
                    }
                    else
                    {
                        lastCount = count;
                        stable = 0;
                    }
                }
            }
            await Task.Delay(600, ct);
        }
        throw new CoreException("BRUSH_PAGE_TIMEOUT", "章节学习页加载超时，请稍后重试", false);
    }

    // ---------- 单章节任务点 ----------

    sealed record ChapterResult(int Processed, bool Unfinished, bool Unknown);

    static async Task<ChapterResult> ProcessChapterAsync(CdpClient cdp, JsonObject chapter, JsonObject page, CancellationToken ct)
    {
        var skip = new HashSet<string>(StringComparer.Ordinal);
        var planned = new HashSet<string>(StringComparer.Ordinal);
        var tasksKnown = false;
        if (chapter["tasks"] is JsonArray tasks)
        {
            tasksKnown = tasks.Count > 0;
            foreach (var task in tasks.OfType<JsonObject>())
            {
                var kind = task["kind"]?.GetValue<string>() ?? "";
                var jobid = task["jobid"]?.GetValue<string>() ?? "";
                if (jobid.Length == 0) continue;
                if (BrushableKinds.Contains(kind) || kind == "other") planned.Add(jobid);
                else skip.Add(jobid);
            }
        }

        Dbg("chapter plan: tasks=" + (chapter["tasks"] as JsonArray)?.Count
            + " planned=[" + string.Join(",", planned) + "] skip=[" + string.Join(",", skip) + "]");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var processed = 0;
        var unfinished = false;
        var unknown = false;

        // 一个章节可能按任务类型分成多个页签（视频 / 文档 / …），每个页签是独立的 cards 页；
        // 逐个进入并处理，以后即使再增加第三种、第四种任务类型也能自动覆盖
        var tabs = page["tabs"] as JsonArray ?? new JsonArray();
        var tabCount = Math.Min(Math.Max(tabs.Count, 1), 6);
        Dbg("chapter tabs=" + tabs.Count + " [" +
            string.Join("|", tabs.OfType<JsonObject>().Select(t =>
                (t["text"]?.GetValue<string>() ?? "") + (t["active"]?.GetValue<bool>() == true ? "*" : ""))) + "]");

        var frames = page["frames"] as JsonArray ?? new JsonArray();
        for (var tab = 0; tab < tabCount; tab++)
        {
            ct.ThrowIfCancellationRequested();
            if (tab > 0)
            {
                var clicked = await ClickTabAsync(cdp, tab);
                Dbg("  tab " + tab + " click " + (clicked?.ToJsonString() ?? "null"));
                if (clicked == null) continue;
                var oldUrl = clicked["oldUrl"]?.GetValue<string>() ?? "";
                if (clicked["already"]?.GetValue<bool>() != true && oldUrl.Length > 0)
                    await WaitCardsChangedAsync(cdp, oldUrl, ct);
                await Task.Delay(600, ct);
                try
                {
                    page = await WaitStudyAsync(cdp, ct);
                    frames = page["frames"] as JsonArray ?? new JsonArray();
                    await MuteAllAsync(cdp);
                }
                catch (CoreException ex) when (!BrowserGone(ex) && ex.Code != "SESSION_EXPIRED")
                {
                    Dbg("  tab " + tab + " load failed: " + ex.Code);
                    continue;
                }
            }

            // 默认页签以规划器清单为准；其余页签（文档等）本身即任务点，不受清单限制
            var r = await ProcessFramesAsync(cdp, frames, planned, skip, tasksKnown, tab > 0, seen, ct);
            processed += r.Processed;
            unfinished |= r.Unfinished;
            unknown |= r.Unknown;
        }

        // 计划里的可刷任务在页面上都没找到：不能据此判定“已刷完”
        foreach (var jobid in planned)
            if (!seen.Contains(jobid)) unknown = true;
        if (!tasksKnown) unknown = true;

        return new ChapterResult(processed, unfinished, unknown);
    }

    static async Task<ChapterResult> ProcessFramesAsync(CdpClient cdp, JsonArray frames, HashSet<string> planned,
        HashSet<string> skip, bool tasksKnown, bool allowUnplanned, HashSet<string> seen, CancellationToken ct)
    {
        var processed = 0;
        var unfinished = false;
        var unknown = false;

        foreach (var frame in frames.OfType<JsonObject>())
        {
            ct.ThrowIfCancellationRequested();
            var path = frame["path"] as JsonArray;
            if (path == null || path.Count == 0) continue;
            var label = string.Join("/", path.Select(p => p?.GetValue<int>() ?? -1));
            // 部分文档模块（如 pdf 查看器）不在 iframe data 上写 jobid，退回读模块 window.jobid
            var jobid = frame["jobid"]?.GetValue<string>() ?? "";
            if (jobid.Length == 0) jobid = frame["wjobid"]?.GetValue<string>() ?? "";
            var frameDone = frame["done"]?.GetValue<bool>() == true;
            if (jobid.Length > 0) seen.Add(jobid);
            if (frameDone)
            {
                Dbg("  frame " + label + " jobid=" + jobid + " already finished (ans-job-finished)");
                continue;
            }
            if (jobid.Length == 0)
            {
                Dbg("  frame " + label + " no jobid, skip");
                continue;      // 非任务点：不操作
            }
            if (skip.Contains(jobid))
            {
                Dbg("  frame " + label + " jobid=" + jobid + " non-brushable, skip");
                continue;   // 做题 / 外链等：跳过
            }
            // 默认页签只处理规划出的可刷任务点，避免把普通资源当成任务点播放
            if (!allowUnplanned && tasksKnown && !planned.Contains(jobid))
            {
                Dbg("  frame " + label + " jobid=" + jobid + " not a task point, skip");
                continue;
            }

            var probe = await ProbeAsync(cdp, path, ct);
            Dbg("  frame " + label + " jobid=" + jobid + " done=" + (probe["done"]?.GetValue<bool>() == true)
                + " kind=" + (probe["kind"]?.GetValue<string>() ?? "") + " cur=" + (probe["cur"]?.GetValue<double>() ?? 0)
                + " dur=" + (probe["dur"]?.GetValue<double>() ?? 0)
                + " err=" + (probe["playErr"]?.GetValue<string>() ?? ""));
            if (probe["done"]?.GetValue<bool>() == true) continue;   // 已刷：跳过
            var kind = probe["kind"]?.GetValue<string>() ?? "other";
            if (!BrushableKinds.Contains(kind) && kind != "other")
            {
                // 模块可能仍在初始化，再等一轮
                await Task.Delay(1500, ct);
                probe = await ProbeAsync(cdp, path, ct);
                if (probe["done"]?.GetValue<bool>() == true) continue;
                kind = probe["kind"]?.GetValue<string>() ?? "other";
                if (!BrushableKinds.Contains(kind) && kind != "other")
                {
                    if (planned.Contains(jobid)) unknown = true;
                    continue;
                }
            }

            processed++;
            // 未知模块：复用文档逻辑，滚动到底部 + 尝试 finishJob，按平台常见规则触发完成
            var exec = kind == "doc" || kind == "other"
                ? await KickDocAsync(cdp, path)
                : await PlayAsync(cdp, path);

            var dur = exec?["dur"]?.GetValue<double>() ?? 0;
            Dbg("  exec " + label + " " + kind + " dur=" + dur
                + " ok=" + (exec?["ok"]?.GetValue<bool>() == true)
                + " speed=" + (exec?["speed"]?.GetValue<bool>() == true)
                + " called=" + (exec?["called"]?.GetValue<string>() ?? ""));
            var done = await WaitDoneAsync(cdp, path, kind, TimeoutFor(kind, dur), ct);
            Dbg("  wait " + label + " " + kind + " -> " + (done ? "done" : "unfinished"));
            if (!done) unfinished = true;
        }

        return new ChapterResult(processed, unfinished, unknown);
    }

    static async Task<JsonObject?> ClickTabAsync(CdpClient cdp, int idx)
    {
        var js = "(function(){var lis=document.querySelectorAll('.prev_list li');" +
                 "if(!lis[" + idx + "])return null;" +
                 "var t=document.getElementById('iframe');" +
                 "var oldUrl=t?String(t.src||''):'';" +
                 "if(lis[" + idx + "].classList.contains('active'))return{ok:true,already:true,oldUrl:oldUrl};" +
                 "lis[" + idx + "].click();return{ok:true,already:false,oldUrl:oldUrl};})()";
        try { return await cdp.EvaluateAsync(js, 10000) as JsonObject; }
        catch (CoreException ex) when (!BrowserGone(ex)) { return null; }
    }

    static async Task WaitCardsChangedAsync(CdpClient cdp, string oldUrl, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            JsonObject? p = null;
            try { p = await cdp.EvaluateAsync(StudyProbeJs, 12000) as JsonObject; }
            catch (CoreException ex) when (!BrowserGone(ex)) { }
            catch (CoreException) { throw; }
            var url = p?["cardsUrl"]?.GetValue<string>() ?? "";
            if (url.Length > 0 && url != oldUrl) { await Task.Delay(500, ct); return; }
            await Task.Delay(500, ct);
        }
        Dbg("  wait cards changed timeout");
    }

    static int TimeoutFor(string kind, double dur)
    {
        if (kind == "doc") return 60_000;
        // 未知模块按“滚动到底部”尝试，最多等 60 秒
        if (kind == "other") return 60_000;
        // 视频/音频都按实际时长累积观看，按 1x 留足 1.15 倍余量：完成会提前返回，超时只在失败时生效
        if (dur <= 0)
        {
            // 时长还没读到（播放器未初始化完成）：给足 45 分钟兜底，读到时长后 WaitDoneAsync 会再放宽
            return 45 * 60_000;
        }
        var ms = dur * 1000 * 1.15 + 30_000;
        return (int)Math.Clamp(ms, 60_000, 3_600_000);
    }

    const string ProbeBody = """
        // 注意：greenligth()/greenlight() 是“标记完成”动作（会给父容器加 ans-job-finished），
        // 不能当查询函数调用，否则探测本身就会把任务点标成已完成。
        function fl(){try{var v=w.finishFlag;return v===1||v===true||v==='1'||v==='true'}catch(e){return false}}
        var kind='other';
        try{
          if(w.video&&w.video.player)kind='video';
          else if(w.audio&&w.audio.player)kind='audio';
          else if(typeof w.finishJob==='function'||typeof w.finishFlag!=='undefined')kind='doc';
        }catch(e){}
        var done=fl();
        // 任务点完成的标准标志：模块 iframe 的父容器带 ans-job-finished（真实完成时由模块自己添加）
        try{
          var pe=fr.parentElement;
          if(pe&&String(pe.className).indexOf('ans-job-finished')>=0)done=true;
        }catch(e){}
        var cur=0,dur=0;
        try{
          var p=(w.video&&w.video.player)||(w.audio&&w.audio.player);
          if(p){cur=p.currentTime()||0;dur=p.duration()||w.duration||0;}
          else{dur=w.duration||0;cur=w.currentTime||0;}
        }catch(e){}
        var atEnd=dur>0&&cur>=dur-1;
        return {done:done,kind:kind,cur:cur,dur:dur,atEnd:atEnd,playErr:(w.__cxBrushPlayError||'')};
        """;

    const string PlayBody = """
        var data={};try{data=JSON.parse(fr.getAttribute('data')||'{}')||{};}catch(e){}
        function T(v){return v===true||v===1||v==='1'||v==='true'}
        var speed=T(data.doublespeed);
        try{if(w.nodoublespeed===true||w.nodoublespeed===1)speed=false;}catch(e){}
        var p=(w.video&&w.video.player)||(w.audio&&w.audio.player);
        if(!p)return{ok:false,dur:0,speed:speed};
        // 首次进入时可能尚未加载元数据，主动 load 一次（每个模块只做一次，避免重播时被重置）
        if(!w.__cxBrushLoaded){w.__cxBrushLoaded=true;try{p.load();}catch(e){}}
        var dur=0;try{dur=p.duration()||w.duration||0;}catch(e){}
        // 刷课全程静音：播放器与底层 media 元素都设为静音 + 音量 0
        try{
          p.muted(true);
          var el=p.tech(true).el();
          if(el){el.muted=true;el.volume=0;}
        }catch(e){}
        if(speed){try{p.playbackRate(2);}catch(e){}}
        // 平台按实际观看时长判定完成，不能拖进度条；若上次已停在片尾但未完成，从头重播
        try{if(dur>2&&p.currentTime()>=dur-2)p.currentTime(0);}catch(e){}
        try{
          var pr=p.play();
          if(pr&&pr.catch)pr.catch(function(e){w.__cxBrushPlayError=String(e);});
        }catch(e){w.__cxBrushPlayError=String(e);}
        return{ok:true,dur:dur,speed:speed};
        """;

    const string DocBody = """
        var out={ok:false,called:''};
        try{
          var d=fr.contentDocument;
          if(d){
            var se=d.scrollingElement||d.documentElement;
            if(se)se.scrollTop=se.scrollHeight;
            if(d.body)d.body.scrollTop=d.body.scrollHeight;
            var inner=d.querySelectorAll('iframe');
            for(var i=0;i<inner.length;i++){
              try{
                var idoc=inner[i].contentDocument;
                if(idoc){
                  var ise=idoc.scrollingElement||idoc.documentElement;
                  if(ise)ise.scrollTop=ise.scrollHeight;
                  if(idoc.body)idoc.body.scrollTop=idoc.body.scrollHeight;
                }
              }catch(e){}
            }
          }
          try{w.scrollTo(0,d&&d.body?d.body.scrollHeight:0);}catch(e){}
        }catch(e){}
        try{if(typeof w.finishJob==='function'){w.finishJob();out.ok=true;out.called='finishJob';}}catch(e){out.err=String(e);}
        return out;
        """;

    const string EndBody = """
        try{if(typeof w.ed_complete==='function'){w.ed_complete();}}catch(e){}
        return {ok:true};
        """;

    static string FrameScript(string body, JsonArray path)
    {
        var sb = new StringBuilder();
        sb.Append("(function(){var t=document.getElementById('iframe');var d=t&&t.contentDocument;");
        sb.Append("if(!d)return{done:true,kind:'none'};var doc=d,fr=null;");
        foreach (var p in path)
        {
            var i = p?.GetValue<int>() ?? -1;
            sb.Append("var fs=doc.querySelectorAll('iframe.ans-attach-online');fr=fs[").Append(i).Append("];");
            sb.Append("if(!fr)return{done:true,kind:'none'};doc=fr.contentDocument;");
        }
        sb.Append("var w=fr.contentWindow;if(!w)return{done:true,kind:'blocked'};");
        sb.Append(body).Append("})()");
        return sb.ToString();
    }

    static async Task<JsonObject> ProbeAsync(CdpClient cdp, JsonArray path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return await cdp.EvaluateAsync(FrameScript(ProbeBody, path), 15000) as JsonObject ?? new JsonObject(); }
        catch (CoreException ex) when (!BrowserGone(ex)) { return new JsonObject(); }
    }

    static async Task<JsonObject> PlayAsync(CdpClient cdp, JsonArray path)
    {
        try { return await cdp.EvaluateAsync(FrameScript(PlayBody, path), 20000) as JsonObject ?? new JsonObject(); }
        catch (CoreException ex) when (!BrowserGone(ex)) { return new JsonObject(); }
    }

    static async Task<JsonObject> KickDocAsync(CdpClient cdp, JsonArray path)
    {
        try { return await cdp.EvaluateAsync(FrameScript(DocBody, path), 20000) as JsonObject ?? new JsonObject(); }
        catch (CoreException ex) when (!BrowserGone(ex)) { return new JsonObject(); }
    }

    static async Task<bool> WaitDoneAsync(CdpClient cdp, JsonArray path, string kind, int timeoutMs, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var label = string.Join("/", path.Select(p => p?.GetValue<int>() ?? -1));
        var deadline = started.AddMilliseconds(timeoutMs);
        var nextKick = DateTime.UtcNow.AddSeconds(6);
        var nextLog = DateTime.UtcNow.AddSeconds(15);
        var endCalled = false;
        DateTime? endDeadline = null;
        var playLogged = false;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var probe = await ProbeAsync(cdp, path, ct);
            if (probe["done"]?.GetValue<bool>() == true)
            {
                Dbg(kind + " " + label + " done in " + (int)(DateTime.UtcNow - started).TotalSeconds + "s");
                return true;
            }
            var playErr = probe["playErr"]?.GetValue<string>() ?? "";
            if (!playLogged && playErr.Length > 0)
            {
                Dbg(kind + " play error: " + playErr);
                playLogged = true;
            }

            var cur = probe["cur"]?.GetValue<double>() ?? 0;
            var dur = probe["dur"]?.GetValue<double>() ?? 0;
            // 时长可能要到播放器加载后才知道：发现后按“时长 ×1.15 + 30 秒”放宽截止时间，
            // 避免播放器初始化慢导致视频还没放完就超时切章
            if (dur > 0)
            {
                var full = started.AddMilliseconds(dur * 1000 * 1.15 + 30_000);
                if (full > deadline && full <= started.AddHours(2)) deadline = full;
            }
            if (DateTime.UtcNow >= nextLog)
            {
                Dbg(kind + " " + label + " playing " + cur.ToString("F1") + "/" + dur.ToString("F1"));
                nextLog = DateTime.UtcNow.AddSeconds(15);
            }
            var atEnd = probe["atEnd"]?.GetValue<bool>() == true || (dur > 0 && cur >= dur - 1);
            if (atEnd && kind is "video" or "audio")
            {
                if (!endCalled)
                {
                    // 已到片尾但服务端未回写完成：触发一次播放器结束回调
                    endCalled = true;
                    try { await cdp.EvaluateAsync(FrameScript(EndBody, path), 10000); } catch (CoreException) { }
                }
                // 片尾后留 30 秒等服务端回写；仍无完成标志则按未完成处理，下次重试
                endDeadline ??= DateTime.UtcNow.AddSeconds(30);
                if (DateTime.UtcNow >= endDeadline) return false;
            }

            if (DateTime.UtcNow >= nextKick)
            {
                if (kind == "doc" || kind == "other")
                {
                    await KickDocAsync(cdp, path);
                    nextKick = DateTime.UtcNow.AddSeconds(8);
                }
                else if (!atEnd)
                {
                    // 播放被暂停/被页面打断：重新播放（已到片尾则不再重播，避免从头开始）
                    await PlayAsync(cdp, path);
                    nextKick = DateTime.UtcNow.AddSeconds(10);
                }
            }
            await Task.Delay(1500, ct);
        }
        return false;
    }

    static bool BrowserGone(CoreException ex)
        => ex.Message.Contains("连接已断开", StringComparison.Ordinal)
           || ex.Message.Contains("发送指令失败", StringComparison.Ordinal);

    static void Dbg(string msg)
    {
        try
        {
            Directory.CreateDirectory(Paths.LogsDir);
            File.AppendAllText(Path.Combine(Paths.LogsDir, "brush-debug.log"),
                "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg + Environment.NewLine);
        }
        catch { }
    }
}
