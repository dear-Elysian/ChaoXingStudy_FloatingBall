using System.Text.Json.Nodes;
using CxBall.Shell;

var testParent = Path.GetFullPath(AppContext.BaseDirectory);
var root = Path.Combine(testParent, "cache-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    void Write(string name, int size)
    {
        var p = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, Enumerable.Repeat((byte)42, size).ToArray());
    }
    void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
    Write("browser-profile/EBWebView/Default/Cache/data", 10);
    Write("browser-ai-msedge/Default/Cache/data", 20);
    Write("browser-ai-msedge/Default/Cache/nested/Code Cache/data", 5);
    Write("browser-ai-msedge/Default/Code Cache/data", 30);
    Write("browser-ai-chrome/Profile 1/GPUCache/data", 40);
    string[] protectedFiles = {
        "browser-ai-msedge/Default/Network/Cookies",
        "browser-ai-msedge/Default/Login Data",
        "browser-ai-msedge/Default/Local Storage/Cache/keep",
        "browser-ai-msedge/Default/Session Storage/keep",
        "browser-ai-msedge/Default/IndexedDB/Cache/keep",
        "browser-ai-msedge/component_crx_cache/keep",
        "browser-ai-msedge/ProvenanceData/keep",
        "accounts/account/session.bin", "config/settings.json", "config/ai-key.bin",
        "other-folder/Cache/keep"
    };
    foreach (var name in protectedFiles) Write(name, 7);
    Check(CacheCleaner.SizeBytes(root) == 105, "应合计两个浏览器类型，嵌套缓存不得重复计数，排除非缓存文件");
    Check(!CacheCleaner.Clean(root, p => Path.GetFileName(p) != "browser-ai-msedge"), "运行中跳过须返回未完成");
    Check(File.Exists(Path.Combine(root, "browser-ai-msedge/Default/Cache/data")), "运行中专用浏览器缓存须保留");
    Check(CacheCleaner.SizeBytes(root) == 55, "空闲浏览器缓存应清理成功");
    Check(CacheCleaner.Clean(root, _ => true), "空闲缓存清理应成功");
    Check(CacheCleaner.SizeBytes(root) == 0, "清理后统计应归零");
    foreach (var name in protectedFiles)
        Check(File.ReadAllBytes(Path.Combine(root, name)).SequenceEqual(Enumerable.Repeat((byte)42, 7)), "受保护文件被改动: " + name);

    // 真正经过生产调度入口：达到条件后必须等所有任务释放，再自动补清。
    Paths.DataDir = root;
    Settings.Current = new JsonObject { ["cacheMode"] = "time", ["cacheDays"] = 7, ["lastCacheCleanAt"] = 0L };
    using var completed = new ManualResetEventSlim();
    Settings.OnCleaned = () => completed.Set();
    Write("browser-profile/EBWebView/Default/Cache/pending", 123);
    var ai = CacheCleaner.BeginActivity();
    var brush = CacheCleaner.BeginActivity();
    CacheCleaner.CheckAndClean();
    Check(CacheCleaner.SizeBytes(root) == 123, "任务运行时应延后所有缓存清理");
    Check(Settings.Current["lastCacheCleanAt"]!.GetValue<long>() == 0, "延后时不推进清理时间");
    ai.Dispose();
    ai.Dispose();
    Check(CacheCleaner.SizeBytes(root) == 123 && !completed.IsSet, "仍有任务时不得清理，重复释放不应减少活动数");
    brush.Dispose();
    Check(completed.Wait(TimeSpan.FromSeconds(5)), "最后任务结束后应自动补清，无需刷新");
    Check(CacheCleaner.SizeBytes(root) == 0, "延后清理必须实际删除缓存");

    completed.Reset();
    Settings.Current["lastCacheCleanAt"] = 0L;
    Write("browser-profile/EBWebView/Default/Cache/failed-task", 19);
    try
    {
        using var failedTask = CacheCleaner.BeginActivity();
        CacheCleaner.CheckAndClean();
        throw new InvalidOperationException("模拟失败或取消后的 finally 释放");
    }
    catch (InvalidOperationException) { }
    Check(completed.Wait(TimeSpan.FromSeconds(5)), "任务异常结束也应补清");
    Check(CacheCleaner.SizeBytes(root) == 0, "异常收尾后缓存清理");

    completed.Reset();
    // 尚未到期时手动清理仍执行，成功后刷新时间戳。
    Settings.Current["lastCacheCleanAt"] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
    Write("browser-profile/EBWebView/Default/Cache/manual", 41);
    var beforeManual = Settings.Current["lastCacheCleanAt"]!.GetValue<long>();
    Check(CacheCleaner.RequestClean()["kind"]!.GetValue<string>() == "done", "手动清理绕过周期");
    Check(CacheCleaner.SizeBytes(root) == 0, "手动清理应删除缓存");
    Check(Settings.Current["lastCacheCleanAt"]!.GetValue<long>() >= beforeManual, "手动成功后从完成时重新计时");

    completed.Reset();
    Write("browser-profile/EBWebView/Default/Cache/manual-pending", 42);
    var manualActivity = CacheCleaner.BeginActivity();
    var oldTime = Settings.Current["lastCacheCleanAt"]!.GetValue<long>();
    Check(CacheCleaner.RequestClean()["kind"]!.GetValue<string>() == "pending", "手动清理也应等待任务结束");
    Check(Settings.Current["lastCacheCleanAt"]!.GetValue<long>() == oldTime, "手动排队时不重置时间戳");
    manualActivity.Dispose();
    Check(completed.Wait(TimeSpan.FromSeconds(5)), "手动排队后任务退出应补清");
    Check(CacheCleaner.SizeBytes(root) == 0, "手动补清不受原周期影响");

    Write("browser-profile/EBWebView/Default/Cache/disabled", 43);
    Settings.Current["cacheEnabled"] = false;
    Settings.Current["lastCacheCleanAt"] = 0L;
    CacheCleaner.CheckAndClean();
    Check(CacheCleaner.RequestClean()["kind"]!.GetValue<string>() == "disabled", "关闭开关时后端也拒绝手动清理");
    Check(CacheCleaner.SizeBytes(root) == 43 && Settings.Current["lastCacheCleanAt"]!.GetValue<long>() == 0, "关闭时不删除、不改时间戳");
    Settings.Current["cacheEnabled"] = true;
    Check(CacheCleaner.RequestClean()["kind"]!.GetValue<string>() == "done", "重新启用可清理");

    // 普通 Edge 存在、失效标记均不应阻止空闲清理。回归旧版按进程/端口误挡。
    Write("browser-ai-msedge/Default/Cache/idle", 44);
    var marker = Path.Combine(root, "config/ai-browser-msedge.json");
    File.WriteAllText(marker, "invalid stale marker");
    Check(CacheCleaner.RequestClean()["kind"]!.GetValue<string>() == "done", "残留标记不能误挡空闲清理");
    Check(CacheCleaner.SizeBytes(root) == 0, "专用缓存空闲时直接清理");

    // 锁住一个文件，其余文件仍应删除，提示文件占用而非有任务正在运行。
    Write("browser-ai-msedge/Default/Cache/locked", 45);
    Write("browser-ai-msedge/Default/Cache/unlocked", 46);
    var lockedPath = Path.Combine(root, "browser-ai-msedge/Default/Cache/locked");
    using (var held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        var partial = CacheCleaner.RequestClean();
        Check(partial["kind"]!.GetValue<string>() == "pending", "占用文件保留待清理");
        Check(partial["reason"]!.GetValue<string>() == "files_in_use", "占用提示必须明确");
        Check(!File.Exists(Path.Combine(root, "browser-ai-msedge/Default/Cache/unlocked")), "单个锁文件不得阻止其余缓存删除");
    }
    Check(CacheCleaner.RequestClean()["kind"]!.GetValue<string>() == "done", "文件释放后可完成补清");

    // 大小模式超过门槛时立即检查即可清，不依赖时间周期。
    var sizeCache = Path.Combine(root, "browser-profile/EBWebView/Default/Cache/large");
    Directory.CreateDirectory(Path.GetDirectoryName(sizeCache)!);
    using (var large = File.Create(sizeCache)) large.SetLength(2 * 1_048_576);
    Settings.Current["cacheMode"] = "size";
    Settings.Current["cacheLimitMB"] = 1;
    CacheCleaner.CheckAndClean();
    Check(!File.Exists(sizeCache), "大小门槛达到后检查应立即删除");
    Console.WriteLine("PASS: combined cache statistics, active-profile skip and preservation of login/API/account data");
    Console.WriteLine("PASS: deferred cleanup waits for all tasks, retries on completion and releases on failure");
    Console.WriteLine("PASS: manual cleanup bypasses schedule, defers safely, resets completion time and respects enable switch");
    Console.WriteLine("PASS: stale marker ignored, locked file does not block others, size mode clears above threshold");
}
finally
{
    var target = Path.GetFullPath(root);
    if (!target.StartsWith(testParent, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(target).StartsWith("cache-fixture-")) throw new Exception("Invalid fixture cleanup target");
    Directory.Delete(target, true);
}

namespace CxBall.Shell
{
    static class Paths { public static string DataDir { get; set; } = ""; }
    static class Settings
    {
        public static JsonObject Current { get; set; } = new();
        public static Action? OnCleaned;
        public static JsonObject Get() => Current;
        public static void Set(string key, long value)
        {
            Current[key] = value;
            OnCleaned?.Invoke();
        }
    }
}
