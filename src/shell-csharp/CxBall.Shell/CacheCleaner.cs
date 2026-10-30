using System.IO;

namespace CxBall.Shell;

/// <summary>
/// 缓存清理（方案 §7.6）：仅清 Cache 类目录，绝不动 Cookies / Login Data / Local Storage。
/// 检查时机 = 软件启动时 + 每次刷新完成后（无常驻定时器）。两种方案互斥：
/// time = 距上次清理超过 N 天则清；size = 缓存总量超过阈值 MB 则清。
/// </summary>
static class CacheCleaner
{
    static readonly string[] CacheDirNames = { "Cache", "Code Cache", "GPUCache", "Media Cache", "ShaderCache" };

    /// <summary>按当前设置检查，满足条件则清理。任何失败静默跳过（文件可能被 WebView2 占用）。</summary>
    public static void CheckAndClean()
    {
        try
        {
            var s = Settings.Get();
            var mode = s["cacheMode"]?.GetValue<string>() ?? "time";
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            bool need = mode == "size"
                ? SizeMB() >= (s["cacheLimitMB"]?.GetValue<int>() ?? 200)
                : now - (s["lastCacheCleanAt"]?.GetValue<long>() ?? 0)
                    >= (s["cacheDays"]?.GetValue<int>() ?? 7) * 86_400_000L;
            if (!need) return;
            Clean();
            Settings.Set("lastCacheCleanAt", now);
        }
        catch { }
    }

    /// <summary>当前网页缓存总量（MB）。</summary>
    public static long SizeMB() => SizeBytes() / 1_048_576;

    static long SizeBytes()
    {
        long total = 0;
        foreach (var dir in FindCacheDirs())
            try
            {
                total += new DirectoryInfo(dir)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
            }
            catch { }
        return total;
    }

    static void Clean()
    {
        foreach (var dir in FindCacheDirs())
            try { Directory.Delete(dir, true); } catch { }
    }

    /// <summary>在 browser-profile 下定位 Cache 类目录（WebView2 实际结构为 EBWebView/Default/…）。</summary>
    static IEnumerable<string> FindCacheDirs()
    {
        if (!Directory.Exists(Paths.ProfileDir)) yield break;
        foreach (var name in CacheDirNames)
        {
            IEnumerable<string> hits;
            try { hits = Directory.EnumerateDirectories(Paths.ProfileDir, name, SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var h in hits) yield return h;
        }
    }
}
