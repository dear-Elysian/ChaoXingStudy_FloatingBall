using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Wpf;

namespace CxBall.Shell;

/// <summary>
/// 前端 ↔ C# ↔ Java 桥。
/// 前端消息：{id, action, payload?}；回包：{id, ok, payload|error:{code,message}}；事件：{event, payload}。
/// 动作：app.init / login.open / logout / refresh / settings.get / settings.set /
///       ignore.set / courses.setDeselected / badge。
/// </summary>
sealed class Bridge
{
    readonly WebView2 _web;
    readonly JavaCore _core;
    readonly MainWindow _main;
    readonly SemaphoreSlim _refreshLock = new(1, 1);   // 防重入（§4.4）

    /// <summary>首次登录默认勾选的课程数（§7.4）：只请求前 N 门详情，其余仅展示课程名。</summary>
    const int DefaultSelectedCourses = 10;

    public Bridge(WebView2 web, JavaCore core, MainWindow main)
    {
        _web = web;
        _core = core;
        _main = main;
        _core.OnEvent += e => Post(new JsonObject { ["event"] = e["type"]?.GetValue<string>(), ["payload"] = e["payload"]?.DeepClone() });
    }

    public void Handle(string json)
    {
        JsonObject? msg;
        try { msg = JsonNode.Parse(json) as JsonObject; } catch { return; }
        if (msg == null) return;
        var id = msg["id"]?.GetValue<string>() ?? "";
        var action = msg["action"]?.GetValue<string>() ?? "";
        var payload = msg["payload"] as JsonObject ?? new JsonObject();
        _ = Dispatch(id, action, payload);
    }

    async Task Dispatch(string id, string action, JsonObject payload)
    {
        try
        {
            var result = action switch
            {
                "app.init" => await AppInit(),
                "login.open" => await LoginOpen(),
                "logout" => await Logout(),
                // 刷新：payload.login=true 表示「登录事件」触发（走登录冷却，§4.2）
                "refresh" => await Refresh(manual: !(payload["login"]?.GetValue<bool>() ?? false)),
                "settings.get" => Settings.Get(),
                "settings.set" => SettingsSet(payload),
                "ignore.set" => IgnoreSet(payload),
                "detail.open" => DetailOpen(payload),
                "ai.status" => AiService.Status(),
                "ai.set" => AiService.SetConfig(payload),
                "ai.key.set" => AiService.SetKey(payload),
                "ai.detect" => await AiService.DetectAsync(),
                "ai.test" => await AiService.TestAsync(),
                "ai.solve" => await AiSolve(payload),
                "browser.pick" => await BrowserPick(),
                "browser.detect" => BrowserDetect(),
                "courses.setDeselected" => CoursesSetDeselected(payload),
                "window.resize" => ResizeWindow(payload),
                "badge" => Badge(payload),
                "cache.stats" => CacheStats(),
                "app.autostart" => AutostartSet(payload),
                "assets.upload" => AssetsUpload(payload),
                "assets.reset" => AssetsReset(payload),
                "accounts.stats" => AccountsStats(),
                "clear.surplus" => ClearSurplus(),
                "clear.allAccounts" => await ClearAllAccounts(),
                "clear.allData" => await ClearAllData(),
                _ => throw new CoreException("UNKNOWN_TYPE", "未知动作: " + action, false),
            };
            Respond(id, true, result);
        }
        catch (CoreException ex)
        {
            Respond(id, false, null, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            Respond(id, false, null, "INTERNAL", ex.Message);
        }
    }

    // ---------- 动作实现 ----------

    async Task<JsonObject> AppInit()
    {
        // §8.2.1：不再在启动时自动探测浏览器；浏览器路径由用户在设置页手动填写/选择，
        // 或点击「自动」显式探测。路径为空时「详情」直接提示去设置。
        var s = Settings.Get();
        var acct = s["currentAccount"]?.GetValue<string>();
        var resp = new JsonObject { ["settings"] = s, ["loggedIn"] = false };
        if (acct == null) return resp;

        var cookiesJson = AccountStore.TryLoadSession(acct);
        if (cookiesJson == null) return resp;

        // §3.3：启动先校验会话；只有确认失效才退回登录界面。
        // 网络故障/超时不算失效，保留登录态让用户自行刷新，避免断网即被踢出登录。
        try
        {
            await PushCookies(JsonNode.Parse(cookiesJson) as JsonArray ?? new JsonArray());
            if (!await _core.ValidateSession())
            {
                Settings.Set("currentAccount", null);
                resp["sessionExpired"] = true;
                return resp;
            }
        }
        catch { /* 网络异常：保留会话 */ }

        var meta = AccountStore.LoadMeta(acct);
        resp["loggedIn"] = true;
        resp["account"] = new JsonObject
        {
            ["id"] = acct,
            ["meta"] = meta,
            ["state"] = AccountStore.LoadState(acct),
        };
        resp["snapshot"] = AccountStore.LoadSnapshot(acct);
        // 元数据缺失时后台补抓（§3.1），抓到后推 account.updated 事件给前端
        if (meta?["nickname"] == null)
            _ = Task.Run(async () =>
            {
                try { await FetchAndSaveMeta(acct); }
                catch { }
            });
        return resp;
    }

    async Task<JsonObject> LoginOpen()
    {
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _main.Dispatcher.InvokeAsync(() =>
        {
            var win = new LoginWindow { Owner = _main, Icon = App.LoadAppIcon() };
            // 登录窗内提交/操作期间不显示进度条，仅保留状态通知（§8.1）
            win.Submitting += () => SendEvent("login.submitting", new JsonObject());
            win.LoggedIn += (uid, cookies) =>
            {
                var hash = AccountStore.HashUid(uid);
                // Cookie 加密落盘（DPAPI）；meta 仅存 uid 与登录时间，昵称头像后续补充
                AccountStore.SaveSession(hash, cookies.ToJsonString());
                AccountStore.SaveMeta(hash, new JsonObject
                {
                    ["uid"] = uid,
                    ["loggedAt"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                });
                Settings.Set("currentAccount", hash);
                // §7.3：每个账号首次登录（尚无课程选择记录）只默认开作业与消息，
                // 主题讨论/随堂练习/考试默认不勾选，减少首刷请求；老账号沿用已存设置。
                var state = AccountStore.LoadState(hash);
                if (state["deselectedCourses"] == null)
                {
                    Settings.Patch(new JsonObject
                    {
                        ["modules"] = new JsonObject
                        {
                            ["assignment"] = true,
                            ["notice"] = true,
                            ["exam"] = false,
                            ["practice"] = false,
                            ["discussion"] = false,
                        },
                    });
                }
                tcs.TrySetResult(new JsonObject { ["account"] = hash, ["cookies"] = cookies });
            };
            win.Closed += (_, _) => tcs.TrySetResult(new JsonObject { ["account"] = null });
            win.ShowDialog();
        });

        var r = await tcs.Task;
        if (r["account"] == null)
            return new JsonObject { ["loggedIn"] = false };   // 登录失败/取消：不占冷却

        // 登录窗已关闭、回到程序后才开始显示进度条（§8.1）
        LoginPhase(18, "登录成功，正在进入程序…");

        // 登录事件：推 Cookie 给 Java（仅内存），按设置触发登录刷新
        var cookies = (JsonArray)r["cookies"]!;
        r.Remove("cookies");
        LoginPhase(26, "正在校验登录状态…");
        await PushCookies(cookies);
        LoginPhase(34, "正在获取账号信息…");
        await FetchAndSaveMeta(r["account"]!.GetValue<string>());   // §3.1：登录成功抓取昵称/头像/学校

        var s = Settings.Get();
        if (s["loginAutoRefresh"]?.GetValue<bool>() != false)
        {
            LoginPhase(40, "正在获取课程列表…");
            var snap = await Refresh(manual: false, suppressCooldownError: true, loginProgress: true);
            LoginPhase(99, "正在载入界面…");
            return new JsonObject
            {
                ["loggedIn"] = true,
                ["snapshot"] = snap["snapshot"]?.DeepClone(),
                ["noticeDays"] = snap["noticeDays"]?.GetValue<int>(),
            };
        }
        LoginPhase(99, "正在载入界面…");
        return new JsonObject { ["loggedIn"] = true };
    }

    async Task<JsonObject> Logout()
    {
        var acct = Settings.Get()["currentAccount"]?.GetValue<string>();
        if (acct != null)
        {
            AccountStore.DeleteSession(acct);          // 保留历史快照（§3.4）
            Settings.Set("currentAccount", null);
        }
        // 清浏览器 Profile 中的登录数据（§3.4）：Cookie + DOM Storage 都属于学习通登录态
        if (_web.CoreWebView2 != null)
        {
            await _web.CoreWebView2.Profile.ClearBrowsingDataAsync(
                Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.Cookies
                | Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.AllDomStorage);
        }
        return new JsonObject();
    }

    /// <summary>刷新主流程：冷却判定 → 推 Cookie → 课程列表 → 逐课程作业+收件箱 → 快照落盘。</summary>
    async Task<JsonObject> Refresh(bool manual, bool suppressCooldownError = false, bool loginProgress = false)
    {
        var acct = Settings.Get()["currentAccount"]?.GetValue<string>()
            ?? throw new CoreException("SESSION_EXPIRED", "尚未登录", false);

        if (!await _refreshLock.WaitAsync(0))
            throw new CoreException("RATE_LIMITED", "刷新进行中，请稍候", true);
        try
        {
            var s = Settings.Get();
            var state = AccountStore.LoadState(acct);
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            // §7.4.1：新账号首次刷新固定只展示 30 天（与课程首次固定前 10 门同理）；
            // 已存在课程选择说明该账号此前已完成过首次登录，直接按设置值处理。
            var firstNoticeLoad = state["noticeDaysInitialized"] == null && state["deselectedCourses"] == null;
            var noticeDays = firstNoticeLoad ? 30 : (s["noticeDays"]?.GetValue<int>() ?? 30);

            // 冷却/节流判定（§4.2，按账号隔离）
            var throttleSec = manual
                ? s["manualThrottleSec"]?.GetValue<int>() ?? 15
                : s["loginRefreshIntervalSec"]?.GetValue<int>() ?? 15;
            var lastKey = manual ? "lastManualRefreshAt" : "lastLoginRefreshAt";
            var last = state[lastKey]?.GetValue<long>() ?? 0;
            var remaining = (int)Math.Ceiling((last + throttleSec * 1000L - now) / 1000.0);
            if (remaining > 0)
            {
                if (suppressCooldownError)
                    return SnapshotReply(acct);   // 命中冷却：沿用旧快照
                throw new CoreException("RATE_LIMITED", $"{remaining}s 后可再次刷新", true);
            }

            // Cookie 从 DPAPI 解密 → 仅内存推给 Java
            var cookiesJson = AccountStore.TryLoadSession(acct)
                ?? throw new CoreException("SESSION_EXPIRED", "会话不存在，请重新登录", false);
            await PushCookies(JsonNode.Parse(cookiesJson) as JsonArray ?? new JsonArray());

            // 阶段一：课程列表（单独调用，便于按勾选过滤后再请求各课程数据）
            // §7.4：首次登录默认只勾选前 10 门（不足则全选），其余仅保留课程名、不请求详情，避免首刷过慢。
            //       仅当 deselectedCourses 尚不存在时视为首次（用户手动全选后会是空数组，不会误判）；
            //       此后一律以磁盘保存的 deselectedCourses 为准，新出现的课程仍默认选中。
            var courses = await _core.CourseList();
            if (loginProgress) LoginPhase(45, "正在获取作业与随堂练习…");
            var deselected = state["deselectedCourses"] as JsonArray;
            if (deselected == null)
            {
                deselected = new JsonArray();
                var all = courses.Select(c => c?["courseId"]?.GetValue<string>()).Where(x => x != null).ToList();
                foreach (var cid in all.Skip(DefaultSelectedCourses))
                    deselected.Add(cid);
                state["deselectedCourses"] = deselected.DeepClone();
                AccountStore.SaveState(acct, state);
            }
            var selected = new JsonArray();
            foreach (var c in courses)
            {
                var cid = c?["courseId"]?.GetValue<string>();
                if (cid == null) continue;
                if (!deselected.Any(d => d?.GetValue<string>() == cid))
                    selected.Add(cid);
            }

            // 阶段二：按「模块显示」开关只抓已启用模块 + 已选课程（§7.3/§7.4）。
            // 作业锁定常显，始终抓取；随堂练习/消息/考试未勾选时不发请求（不刷新）。
            // 即使一门课程都没选，也要调用 fetch.start：收件箱是全局数据，不随课程勾选停更。
            // courseIds 显式空数组 = 不抓课程数据；缺省才表示全部课程。
            var mods = s["modules"] as JsonObject;
            bool ModuleOn(string key) => mods?[key]?.GetValue<bool>() != false;
            var practiceOn = ModuleOn("practice");
            var noticeOn = ModuleOn("notice");
            var examOn = ModuleOn("exam");
            var discussionOn = ModuleOn("discussion");
            var modules = new JsonArray("assignment");
            if (practiceOn) modules.Add("practice");
            if (noticeOn) modules.Add("notice");
            if (examOn) modules.Add("exam");
            if (discussionOn) modules.Add("discussion");
            var result = await _core.FetchStart(modules, selected);
            // 优先使用 Java 返回的课程列表：已选课程带 stuenc/enc，随快照落盘（附录 B.4）；
            // 课程阶段失败时回落到阶段一列表，保证设置页仍能看到课程名。
            if (result["courses"] is not JsonArray fetched || fetched.Count == 0)
                result["courses"] = courses;

            // 未启用模块不抓取，但保留上一次快照里的同类条目：开关只是显示控制，
            // 重新勾选时无需等下一次刷新就能看到上次的数据（不会被误当成刚刷新）。
            var items = result["items"] as JsonArray ?? new JsonArray();
            if (AccountStore.LoadSnapshot(acct)?["items"] is JsonArray oldItems)
            {
                foreach (var node in oldItems)
                {
                    var type = node?["type"]?.GetValue<string>();
                    if ((type == "practice" && !practiceOn) || (type == "notice" && !noticeOn)
                        || (type == "exam" && !examOn) || (type == "discussion" && !discussionOn))
                        items.Add(node?.DeepClone());
                }
            }

            var snapshot = new JsonObject
            {
                ["fetchedAt"] = result["fetchedAt"]?.DeepClone(),
                ["courses"] = result["courses"]?.DeepClone(),
                ["items"] = items.DeepClone(),   // 必须克隆：items 仍挂在 result 上，直接放入新对象会触发 “node already has a parent”
                ["failures"] = result["failures"]?.DeepClone(),
            };
            AccountStore.SaveSnapshot(acct, snapshot);
            state[lastKey] = now;   // 失败也占冷却（§4.3）：此处为成功路径；失败在 catch 里同样写
            state["noticeDaysInitialized"] = true;   // 首次 30 天策略只生效一次
            AccountStore.SaveState(acct, state);
            // §3.2：meta.json 记录上次刷新时间（界面「上次刷新」以快照 fetchedAt 为准）
            var meta = AccountStore.LoadMeta(acct) ?? new JsonObject();
            meta["lastRefreshAt"] = now;
            AccountStore.SaveMeta(acct, meta);
            CacheCleaner.CheckAndClean();   // §7.6：每次刷新完成后检查缓存
            return new JsonObject { ["snapshot"] = snapshot, ["noticeDays"] = noticeDays };
        }
        catch (CoreException ex)
        {
            // 刷新失败占用冷却（§4.3）；防重入/冷却命中不算失败，不改时间戳
            if (ex.Code != "RATE_LIMITED")
            {
                var state = AccountStore.LoadState(acct);
                state[manual ? "lastManualRefreshAt" : "lastLoginRefreshAt"] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                AccountStore.SaveState(acct, state);
            }
            throw;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    JsonObject SnapshotReply(string acct)
        => new() { ["snapshot"] = AccountStore.LoadSnapshot(acct) };

    async Task PushCookies(JsonArray cookies)
    {
        string? ua = null;
        try { ua = _web.CoreWebView2?.Settings.UserAgent; } catch { }
        await _core.SetCookies(cookies, ua);
    }

    JsonObject SettingsSet(JsonObject payload)
    {
        if (payload["patch"] is JsonObject patch) Settings.Patch(patch);
        return Settings.Get();
    }

    /// <summary>开机自启动开关（§7.5）：写注册表 Run 项 + 落盘设置，默认关闭。</summary>
    JsonObject AutostartSet(JsonObject payload)
    {
        var enabled = payload["enabled"]?.GetValue<bool>() ?? false;
        Autostart.Apply(enabled);
        Settings.Set("autostart", enabled);
        return Settings.Get();
    }

    JsonObject IgnoreSet(JsonObject payload)
    {
        var acct = CurrentAccount();
        var key = payload["key"]?.GetValue<string>() ?? throw new CoreException("INTERNAL", "缺少 key", false);
        var ignored = payload["ignored"]?.GetValue<bool>() ?? true;
        var state = AccountStore.LoadState(acct);
        var list = state["ignored"] as JsonArray;
        if (list == null)
        {
            list = new JsonArray();
            state["ignored"] = list;   // 仅新建时挂载；已挂载的数组原地修改即可，重复赋值会抛节点重复挂载异常
        }
        for (int i = list.Count - 1; i >= 0; i--)
            if (IgnoreKeyOf(list[i]) == key) list.RemoveAt(i);
        // 记录忽略时间（§8.2.1「已忽略」按操作时间近→远排序）
        if (ignored)
            list.Add(new JsonObject { ["key"] = key, ["ts"] = DateTimeOffset.Now.ToUnixTimeMilliseconds() });
        AccountStore.SaveState(acct, state);
        return new JsonObject { ["ignored"] = list.DeepClone() };
    }

    /// <summary>忽略条目兼容旧格式（纯字符串 key）与新格式（{key,ts}）。</summary>
    static string? IgnoreKeyOf(JsonNode? n)
        => n is JsonValue v ? v.GetValue<string>() : n?["key"]?.GetValue<string>();

    JsonObject CoursesSetDeselected(JsonObject payload)
    {
        var acct = CurrentAccount();
        var state = AccountStore.LoadState(acct);
        state["deselectedCourses"] = payload["courseIds"]?.DeepClone() ?? new JsonArray();
        AccountStore.SaveState(acct, state);
        return new JsonObject();
    }

    /// <summary>边缘/角命中（§8.2.6）：交给系统原生缩放，四边四角均可拖拽。</summary>
    JsonObject ResizeWindow(JsonObject payload)
    {
        _main.BeginResize(payload["edge"]?.GetValue<string>() ?? "");
        return new JsonObject();
    }

    JsonObject Badge(JsonObject payload)
    {
        _main.NotifyBallBadge(payload["unfinished"]?.GetValue<int>() ?? 0);
        return new JsonObject();
    }

    // ---------- 作业详情（§8.2.1：用系统浏览器打开，不使用内嵌 WebView2） ----------

    /// <summary>
    /// 用设置里配置的外部浏览器打开作业详情链接（作业列表 li[data] 原文，见附录 A.2）。
    /// 只允许 chaoxing.com 域，避免把采集到的任意链接直接交给浏览器。
    /// 只认用户配置的路径；为空/失效时返回 BROWSER_NOT_SET，由前端提示去设置页填写或点「自动」。
    /// </summary>
    JsonObject DetailOpen(JsonObject payload) => OpenExternal(ParseChaoxingUrl(payload));

    /// <summary>
    /// AI帮做（§7.9）：打开作答页 → 只读抓题 → 一次批量请求模型 → CDP 写入答案。
    /// kind=practice 时走随堂练习专用流程（题型范围更窄、系统提示词不同）。
    /// 使用应用自己的 Cookie（DPAPI 加密内存解密）登录，不把 Cookie 交给第三方；
    /// 不自动提交，最终由用户在浏览器中检查后手动提交。
    /// </summary>
    async Task<JsonObject> AiSolve(JsonObject payload)
    {
        var uri = ParseChaoxingUrl(payload);
        AiService.EnsureReady();
        var acct = CurrentAccount();
        var cookiesJson = AccountStore.TryLoadSession(acct)
            ?? throw new CoreException("SESSION_EXPIRED", "登录会话不存在，请重新登录", false);

        JsonArray cookies;
        try { cookies = JsonNode.Parse(cookiesJson) as JsonArray ?? new JsonArray(); }
        catch { throw new CoreException("SESSION_EXPIRED", "登录会话已损坏，请重新登录", false); }

        var progress = new Action<string>(text =>
            SendEvent("ai.solve.progress", new JsonObject { ["text"] = text }));
        return payload["kind"]?.GetValue<string>() == "practice"
            ? await AiQuizSolver.SolveAsync(uri, cookies, progress)
            : await AiWorkSolver.SolveAsync(uri, cookies, progress);
    }

    static Uri ParseChaoxingUrl(JsonObject payload)
    {
        var raw = payload["url"]?.GetValue<string>()?.Trim() ?? "";
        if (raw.Length == 0)
            throw new CoreException("INTERNAL", "该条目缺少详情链接，请刷新后重试", false);

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            Uri.TryCreate(new Uri("https://mooc1.chaoxing.com/"), raw, out uri);   // 兼容相对链接
        if (uri == null || !IsChaoxingUrl(uri))
            throw new CoreException("INTERNAL", "详情链接无效", false);
        return uri;
    }

    JsonObject OpenExternal(Uri uri)
    {
        // §8.2.1：只认用户配置的路径，路径为空/失效直接返回 BROWSER_NOT_SET，由前端弹窗引导去设置；
        // 不再自动探测回填（自动探测改为设置页「自动」按钮显式触发）。
        var path = BrowserLauncher.Normalize(Settings.Get()["browserPath"]?.GetValue<string>());
        if (path.Length == 0 || !File.Exists(path))
            throw new CoreException("BROWSER_NOT_SET", "请先在设置页的「详情浏览器」中填写浏览器地址", false);

        try { BrowserLauncher.Open(path, uri.AbsoluteUri); }
        catch (Exception ex) { throw new CoreException("INTERNAL", "无法启动浏览器：" + ex.Message, false); }
        return new JsonObject();
    }

    static bool IsChaoxingUrl(Uri u)
        => (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
           && (u.Host.Equals("chaoxing.com", StringComparison.OrdinalIgnoreCase)
               || u.Host.EndsWith(".chaoxing.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>设置页「选择浏览器」：弹出文件对话框，选中即落盘。</summary>
    async Task<JsonObject> BrowserPick()
    {
        var picked = "";
        await _main.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择浏览器",
                Filter = "浏览器程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(_main) == true) picked = dlg.FileName;
        });
        if (picked.Length > 0) Settings.Set("browserPath", picked);
        return new JsonObject { ["path"] = picked };
    }

    /// <summary>设置页「自动」按钮（§8.2.1）：显式探测本机浏览器并写入 browserPath；找不到返回空路径。</summary>
    JsonObject BrowserDetect()
    {
        var exe = BrowserLauncher.Detect();
        if (exe.Length > 0) Settings.Set("browserPath", exe);
        return new JsonObject { ["path"] = exe };
    }

    /// <summary>缓存状态（§7.6 设置页显示）。</summary>
    JsonObject CacheStats()
        => new() { ["sizeMB"] = CacheCleaner.SizeMB(), ["lastCleanAt"] = Settings.Get()["lastCacheCleanAt"]?.GetValue<long>() ?? 0 };

    /// <summary>推送自定义事件给前端（悬浮球菜单、账号元数据更新等）。</summary>
    public void SendEvent(string name, JsonObject payload)
        => Post(new JsonObject { ["event"] = name, ["payload"] = payload });

    /// <summary>登录进度阶段（§8.1）：百分数只对应真实阶段，不做时间预估。</summary>
    void LoginPhase(int percent, string text)
        => SendEvent("login.phase", new JsonObject { ["percent"] = percent, ["text"] = text });

    /// <summary>抓取账号元数据（昵称/头像/学校）合并落盘并通知前端；失败静默，前端回退显示「账号 uid」。</summary>
    async Task FetchAndSaveMeta(string acct)
    {
        try
        {
            var meta = await _core.FetchMeta();
            if (meta.Count == 0) return;
            var cur = AccountStore.LoadMeta(acct) ?? new JsonObject();
            foreach (var kv in meta) cur[kv.Key] = kv.Value?.DeepClone();
            AccountStore.SaveMeta(acct, cur);
            SendEvent("account.updated", new JsonObject { ["meta"] = cur.DeepClone() });
        }
        catch { }
    }

    string CurrentAccount()
        => Settings.Get()["currentAccount"]?.GetValue<string>()
           ?? throw new CoreException("SESSION_EXPIRED", "尚未登录", false);

    // ---------- 用户图片（背景图 / 悬浮球图案，存 data/assets） ----------

    JsonObject AssetsUpload(JsonObject payload)
    {
        var kind = payload["kind"]?.GetValue<string>() ?? "";
        if (kind is not ("background" or "ball"))
            throw new CoreException("INTERNAL", "未知图片类型: " + kind, false);
        var name = payload["name"]?.GetValue<string>() ?? "img.png";
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            throw new CoreException("INTERNAL", "仅支持 png / jpg / webp 格式", false);
        var b64 = payload["dataBase64"]?.GetValue<string>()
            ?? throw new CoreException("INTERNAL", "缺少图片数据", false);

        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch { throw new CoreException("INTERNAL", "图片数据解码失败", false); }
        if (kind == "ball" && bytes.Length > 15L * 1024 * 1024)
            throw new CoreException("INTERNAL", "悬浮球图案最大 15MB，请更换图片", false);

        Directory.CreateDirectory(Paths.AssetsDir);
        var file = kind + "-" + Guid.NewGuid().ToString("N")[..8] + ext;
        var full = Path.Combine(Paths.AssetsDir, file);
        File.WriteAllBytes(full, bytes);

        // 清旧文件 + 更新设置（Patch 深合并，保留 background.opacity 等其他字段）
        if (kind == "background")
        {
            TryDeleteAsset(Settings.Get()["background"]?["image"]?.GetValue<string>());
            Settings.Patch(new JsonObject { ["background"] = new JsonObject { ["image"] = file } });
        }
        else
        {
            TryDeleteAsset(Settings.Get()["ballImage"]?.GetValue<string>());
            Settings.Set("ballImage", file);
            _main.UpdateBallImage(full);
        }
        return new JsonObject { ["file"] = file };
    }

    JsonObject AssetsReset(JsonObject payload)
    {
        var kind = payload["kind"]?.GetValue<string>() ?? "";
        if (kind == "background")
        {
            TryDeleteAsset(Settings.Get()["background"]?["image"]?.GetValue<string>());
            Settings.Patch(new JsonObject { ["background"] = new JsonObject { ["image"] = null } });
        }
        else if (kind == "ball")
        {
            TryDeleteAsset(Settings.Get()["ballImage"]?.GetValue<string>());
            Settings.Set("ballImage", null);
            _main.UpdateBallImage(null);
        }
        return new JsonObject();
    }

    static void TryDeleteAsset(string? file)
    {
        if (file == null) return;
        try { File.Delete(Path.Combine(Paths.AssetsDir, file)); } catch { }
    }

    // ---------- 账号清除（§7.7） ----------

    JsonObject AccountsStats()
    {
        var cur = Settings.Get()["currentAccount"]?.GetValue<string>();
        var dirs = Directory.Exists(Paths.AccountsDir) ? Directory.GetDirectories(Paths.AccountsDir) : Array.Empty<string>();
        var others = cur == null ? dirs.Length : dirs.Count(d => Path.GetFileName(d) != cur);
        var ignored = cur == null ? 0 : (AccountStore.LoadState(cur)["ignored"] as JsonArray)?.Count ?? 0;
        return new JsonObject { ["total"] = dirs.Length, ["others"] = others, ["ignored"] = ignored };
    }

    /// <summary>清除多余账号：仅删非当前账号目录，保留设置/规则/日志/Profile，保持已登录。</summary>
    JsonObject ClearSurplus()
    {
        var cur = CurrentAccount();
        var removed = 0;
        foreach (var d in Directory.GetDirectories(Paths.AccountsDir))
        {
            if (Path.GetFileName(d) == cur) continue;
            Directory.Delete(d, true);
            removed++;
        }
        return new JsonObject { ["removed"] = removed };
    }

    /// <summary>清除所有账号：删全部账号目录 + Profile 登录数据（Cookie），强制登出；保留设置/规则。</summary>
    async Task<JsonObject> ClearAllAccounts()
    {
        foreach (var d in Directory.GetDirectories(Paths.AccountsDir))
            Directory.Delete(d, true);
        Settings.Set("currentAccount", null);
        if (_web.CoreWebView2 != null)
            await _web.CoreWebView2.Profile.ClearBrowsingDataAsync(
                Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.Cookies |
                Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.AllDomStorage);
        return new JsonObject();
    }

    /// <summary>清除全部数据：清浏览数据 → 回包 → 退出进程 → 外部脚本删整个 data/（文件被占用只能进程退出后删）。</summary>
    async Task<JsonObject> ClearAllData()
    {
        try
        {
            if (_web.CoreWebView2 != null)
                await _web.CoreWebView2.Profile.ClearBrowsingDataAsync();
        }
        catch { }
        // 自清理：等待本进程退出后删除 data 目录（cmd 外部执行，与进程解耦）
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", "/c timeout /t 2 /nobreak >nul & rd /s /q \"" + Paths.DataDir + "\"")
        { CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
        // 先让回包发出去再退出
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            Environment.Exit(0);
        });
        return new JsonObject();
    }

    // ---------- 回包 ----------

    void Respond(string id, bool ok, JsonObject? payload, string? code = null, string? message = null)
    {
        var resp = new JsonObject { ["id"] = id, ["ok"] = ok };
        if (ok) resp["payload"] = payload ?? new JsonObject();
        else resp["error"] = new JsonObject { ["code"] = code, ["message"] = message };
        Post(resp);
    }

    void Post(JsonObject msg)
    {
        try { _main.Dispatcher.Invoke(() => _web.CoreWebView2?.PostWebMessageAsJson(msg.ToJsonString())); }
        catch { }
    }
}
