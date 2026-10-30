using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace CxBall.Shell;

/// <summary>
/// 外部浏览器（§8.2.1）：作业/消息详情交给用户电脑里的浏览器打开，不使用内嵌 WebView2。
/// 仅在设置页点「自动」时探测常见浏览器；用户也可手动填写/选择 exe 路径，路径为空则「详情」提示去设置。
/// </summary>
static class BrowserLauncher
{
    /// <summary>常见浏览器可执行文件名（用于注册表 App Paths 与默认浏览器 ProgId 映射）。</summary>
    static readonly (string Exe, string RelPath)[] Known =
    {
        ("msedge.exe", @"Microsoft\Edge\Application\msedge.exe"),
        ("chrome.exe", @"Google\Chrome\Application\chrome.exe"),
        ("firefox.exe", @"Mozilla Firefox\firefox.exe"),
        ("brave.exe", @"BraveSoftware\Brave-Browser\Application\brave.exe"),
        ("opera.exe", @"Programs\Opera\opera.exe"),
    };

    /// <summary>自动探测：默认浏览器 → 注册表 App Paths → 常见安装目录；找不到返回空字符串。</summary>
    public static string Detect()
    {
        // 1) 系统默认浏览器（HKCU\...\UrlAssociations\https\UserChoice 的 ProgId）
        var progId = ReadString(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice", "ProgId");
        if (!string.IsNullOrEmpty(progId))
        {
            var exe = progId switch
            {
                var p when p.StartsWith("MSEdge", StringComparison.OrdinalIgnoreCase) => "msedge.exe",
                var p when p.StartsWith("Chrome", StringComparison.OrdinalIgnoreCase) => "chrome.exe",
                var p when p.StartsWith("Firefox", StringComparison.OrdinalIgnoreCase) => "firefox.exe",
                var p when p.StartsWith("Brave", StringComparison.OrdinalIgnoreCase) => "brave.exe",
                var p when p.StartsWith("Opera", StringComparison.OrdinalIgnoreCase) => "opera.exe",
                _ => null,
            };
            if (exe != null)
            {
                var path = FromAppPaths(exe);
                if (path.Length > 0) return path;
            }
        }

        // 2) 注册表 App Paths（HKCU 优先于 HKLM）
        foreach (var (exe, _) in Known)
        {
            var path = FromAppPaths(exe);
            if (path.Length > 0) return path;
        }

        // 3) 常见安装目录
        foreach (var (exe, rel) in Known)
        {
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            })
            {
                if (root.Length == 0) continue;
                var p = Path.Combine(root, rel);
                if (File.Exists(p)) return p;
            }
        }
        return "";
    }

    static string FromAppPaths(string exe)
    {
        foreach (var root in new[]
        {
            @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe,
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe,
        })
        {
            var p = ReadString(root, null);
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
        }
        return "";
    }

    static string? ReadString(string key, string? valueName)
    {
        try { return Registry.GetValue(key, valueName, null) as string; }
        catch { return null; }
    }

    /// <summary>规范化用户填写的路径：去引号、展开环境变量；空字符串表示未配置。</summary>
    public static string Normalize(string? raw)
        => Environment.ExpandEnvironmentVariables((raw ?? "").Trim().Trim('"'));

    /// <summary>用指定浏览器打开网址（参数一次只传一个 URL，兼容 Edge/Chrome/Firefox）。</summary>
    public static void Open(string browserPath, string url)
        => Process.Start(new ProcessStartInfo(browserPath, "\"" + url + "\"") { UseShellExecute = true });
}
