using System.IO;

namespace CxBall.Shell;

/// <summary>
/// 目录约定（方案 §6）：app/ 只读程序，data/ 唯一可写。便携版，Root = 项目根。
/// 开发期与发布期都从输出目录向上找含 app/core/core.jar 的目录（发布目录 = 学习通悬浮球\app\）。
/// </summary>
static class Paths
{
    public static readonly string Root = LocateRoot();

    public static string CoreJar     => Path.Combine(Root, "app", "core", "core.jar");
    public static string BundledJava => Path.Combine(Root, "app", "jre", "bin", "java.exe");
    public static string WebDir      => Path.Combine(Root, "app", "resources", "web");
    public static string ResourcesDir => Path.Combine(Root, "app", "resources");
    /// <summary>默认图标 / 悬浮球默认图案（随程序分发，只读，作为「恢复默认」的来源）。</summary>
    public static string DefaultIcon => Path.Combine(ResourcesDir, "icon.png");
    public static string DataDir     => Path.Combine(Root, "data");
    public static string AccountsDir => Path.Combine(DataDir, "accounts");
    public static string ProfileDir  => Path.Combine(DataDir, "browser-profile");
    public static string AssetsDir   => Path.Combine(DataDir, "assets");
    public static string LogsDir     => Path.Combine(DataDir, "logs");
    public static string ConfigFile  => Path.Combine(DataDir, "config", "settings.json");

    static string LocateRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "app", "core", "core.jar")))
                return dir.FullName;
        return Directory.GetCurrentDirectory();
    }

    public static void EnsureDataDirs()
    {
        Directory.CreateDirectory(AccountsDir);
        Directory.CreateDirectory(ProfileDir);
        Directory.CreateDirectory(AssetsDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
    }
}
