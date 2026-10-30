using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace CxBall.Shell;

public partial class App : Application
{
    public static CoreWebView2Environment? WebEnv { get; private set; }
    public static JavaCore Core { get; } = new();

    /// <summary>窗口图标（app/resources/icon.ico 已编译进程序；这里按 png 载入以统一各窗口外观）。</summary>
    public static System.Windows.Media.ImageSource? LoadAppIcon()
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(Paths.DefaultIcon);
            bmp.EndInit();
            return bmp;
        }
        catch { return null; }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Paths.EnsureDataDirs();
        CacheCleaner.CheckAndClean();   // §7.6：启动时检查缓存（此时 Profile 尚未被占用，清理最干净）
        // §7.5：按设置同步开机自启动（便携版路径变化时也能指向当前 exe）
        Autostart.Apply(Settings.Get()["autostart"]?.GetValue<bool>() ?? false);

        // WebView2 Runtime 检测（§11）：缺失时给出下载引导，而不是静默闪退
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch
        {
            var go = MessageBox.Show(
                "未检测到 WebView2 Runtime（微软 Edge 网页组件），软件无法启动。\n\n是否现在打开官方下载页？安装后重新打开本程序即可。",
                "学习通悬浮球", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (go == MessageBoxResult.Yes)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                        UseShellExecute = true,
                    });
                }
                catch { }
            }
            Shutdown(1);
            return;
        }

        // Java 核心进程 + 握手
        try
        {
            Core.Start();
            var hs = Core.Handshake().GetAwaiter().GetResult();
            System.Diagnostics.Debug.WriteLine("core handshake: " + hs.ToJsonString());
        }
        catch (Exception ex)
        {
            MessageBox.Show("Java 核心启动失败：" + ex.Message, "学习通悬浮球",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _ = InitAsync();
    }

    async Task InitAsync()
    {
        // 全局共用一个 WebView2 Profile（data/browser-profile，方案 §6）
        WebEnv = await CoreWebView2Environment.CreateAsync(null, Paths.ProfileDir);

        var ball = new BallWindow();
        var main = new MainWindow(ball);
        ball.MainPanel = main;
        // 窗口图标统一为内置默认图案（标题栏 / 任务栏）
        ball.Icon = main.Icon = LoadAppIcon();
        // 启动时还原悬浮球自定义图案（§7.5）
        var ballImg = Settings.Get()["ballImage"]?.GetValue<string>();
        ball.SetImage(ballImg == null ? null : System.IO.Path.Combine(Paths.AssetsDir, ballImg));
        ball.Show();
        main.PlaceBesideBall();   // 面板位置取决于悬浮球所在象限（§8.2.5）
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Core.Dispose();
        WebEnv = null;
        base.OnExit(e);
    }
}
