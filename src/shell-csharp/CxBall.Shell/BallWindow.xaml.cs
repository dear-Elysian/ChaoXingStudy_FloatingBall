using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CxBall.Shell;

/// <summary>悬浮球：无边框置顶圆形。左键拖拽移动，单击展开/收起主面板，右键菜单退出。</summary>
public partial class BallWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT pt);

    static readonly SolidColorBrush DefaultFill = new(Color.FromRgb(0x3B, 0x82, 0xF6));

    /// <summary>设置/还原悬浮球自定义图案（§7.5，data/assets 内文件；null = 默认图标 app/resources/icon.png）。</summary>
    public void SetImage(string? path)
    {
        // 未自定义时回落到程序内置默认图案（随程序分发，只读，见 Paths.DefaultIcon）
        var file = path != null && File.Exists(path) ? path : Paths.DefaultIcon;
        if (File.Exists(file))
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // 加载后释放文件锁，允许后续覆盖/删除
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            Ball.Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            BallText.Visibility = Visibility.Collapsed;
        }
        else
        {
            Ball.Fill = DefaultFill;
            BallText.Visibility = Visibility.Visible;
        }
    }
    public MainWindow? MainPanel;
    Point _downCursor;          // 按下时的屏幕光标（设备像素）
    double _startLeft, _startTop;
    bool _dragged;

    public BallWindow()
    {
        InitializeComponent();
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Bottom - Height - 120;
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        // 用屏幕光标绝对值做基准，自行设置 Left/Top 移动。
        // 不能调用 DragMove()：那会进入系统移动循环，拖到屏幕边缘会触发 Aero Snap（半屏/全屏）
        // 与顶部「贴靠布局」浮出菜单（§8.3）。
        GetCursorPos(out var p);
        _downCursor = new Point(p.X, p.Y);
        _startLeft = Left;
        _startTop = Top;
        _dragged = false;
        Ball.CaptureMouse();
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (!Ball.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        GetCursorPos(out var p);
        var dxPx = p.X - _downCursor.X;
        var dyPx = p.Y - _downCursor.Y;
        if (!_dragged && Math.Abs(dxPx) < 5 && Math.Abs(dyPx) < 5) return;   // 5px 阈值内视为点击
        _dragged = true;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _startLeft + dxPx / dpi.DpiScaleX;
        Top = _startTop + dyPx / dpi.DpiScaleY;
        ClampToWorkArea();
    }

    /// <summary>
    /// 限制悬浮球最多探出工作区半个身位（§8.3）：贴边时一半在屏幕外，贴角时只剩 1/4 可见。
    /// </summary>
    void ClampToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        Left = Math.Min(Math.Max(Left, wa.Left - Width / 2), wa.Right - Width / 2);
        Top = Math.Min(Math.Max(Top, wa.Top - Height / 2), wa.Bottom - Height / 2);
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        Ball.ReleaseMouseCapture();
        if (!_dragged) ToggleMain();
    }

    void ToggleMain()
    {
        if (MainPanel == null) return;
        // 最小化到任务栏时 IsVisible 仍为 true，但用户看不到面板，
        // 此时点击悬浮球应还原并显示，而不是「隐藏」一次（§8.2.7）
        var shown = MainPanel.IsVisible && MainPanel.WindowState != WindowState.Minimized;
        if (shown) MainPanel.Hide();
        else MainPanel.ShowPanel();
    }

    void ShowMain(object sender, RoutedEventArgs e) => MainPanel?.ShowPanel();

    // 右键菜单动作（§8.3）：经 MainWindow → Bridge 事件 → 前端执行
    void MenuRefresh(object sender, RoutedEventArgs e) => MainPanel?.MenuRefresh();
    void MenuSettings(object sender, RoutedEventArgs e) => MainPanel?.MenuOpenSettings();
    void MenuLogout(object sender, RoutedEventArgs e) => MainPanel?.MenuLogout();

    void QuitApp(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
