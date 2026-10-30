using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CxBall.Shell;

/// <summary>主面板窗口：托管前端页面（app/resources/web/index.html），消息走 Bridge。</summary>
public partial class MainWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    const int SW_RESTORE = 9;
    const int WM_NCLBUTTONDOWN = 0x00A1;
    const int HTLEFT = 10, HTRIGHT = 11, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    readonly BallWindow _ball;
    Bridge? _bridge;
    readonly double _defaultWidth;
    readonly double _defaultHeight;

    Point _downCursor;
    double _startLeft, _startTop;
    bool _dragging;

    public MainWindow(BallWindow ball)
    {
        _ball = ball;
        InitializeComponent();
        _defaultWidth = Width;     // 取自 XAML（400×640）：每次重新打开主面板都回到这个尺寸
        _defaultHeight = Height;
        TitleIcon.Source = App.LoadAppIcon();
        Left = SystemParameters.WorkArea.Right - Width - 100;
        Top = SystemParameters.WorkArea.Bottom - Height - 60;
        Loaded += async (_, _) => { ApplyRoundedCorners(); await InitWeb(); };
    }

    // ---------- 自绘标题栏（§4.5）：拖动不进入系统移动循环，因此不会触发贴靠 ----------

    void TitleBar_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1) return;   // 双击不做最大化（面板禁用最大化）
        GetCursorPos(out var p);
        _downCursor = new Point(p.X, p.Y);
        _startLeft = Left;
        _startTop = Top;
        _dragging = false;
        TitleBar.CaptureMouse();
    }

    void TitleBar_Move(object sender, MouseEventArgs e)
    {
        if (!TitleBar.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        GetCursorPos(out var p);
        var dx = p.X - _downCursor.X;
        var dy = p.Y - _downCursor.Y;
        if (!_dragging && Math.Abs(dx) < 5 && Math.Abs(dy) < 5) return;
        _dragging = true;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _startLeft + dx / dpi.DpiScaleX;
        Top = _startTop + dy / dpi.DpiScaleY;
    }

    void TitleBar_Up(object sender, MouseButtonEventArgs e) => TitleBar.ReleaseMouseCapture();

    void Minimize_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        WindowState = WindowState.Minimized;
    }

    void Close_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    // ---------- 圆角窗口（§8.2.6）：Win11 由 DWM 做原生圆角 ----------

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    const int DwmwaWindowCornerPreference = 33;
    const int DwmcpRound = 2;

    void ApplyRoundedCorners()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var pref = DwmcpRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));
    }

    async Task InitWeb()
    {
        await Web.EnsureCoreWebView2Async(App.WebEnv);
        // 关闭 WebView2 原生右键菜单：只有作业/消息条目有自绘菜单，其余区域右键无反应（§8.2.1）
        Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        // 前端通过 https://cxball.assets/<file> 引用 data/assets 用户图片（背景图/悬浮球图案预览）
        Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "cxball.assets", Paths.AssetsDir,
            Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        // 程序只读资源（默认图标等）
        Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "cxball.app", Paths.ResourcesDir,
            Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        _bridge = new Bridge(Web, App.Core, this);
        Web.CoreWebView2.WebMessageReceived += (_, e) => _bridge.Handle(e.WebMessageAsJson);

        var index = Path.Combine(Paths.WebDir, "index.html");
        if (File.Exists(index))
            Web.CoreWebView2.Navigate(new Uri(index).AbsoluteUri);
        else
            Web.CoreWebView2.NavigateToString(
                "<meta charset='utf-8'><body style='font-family:sans-serif;padding:24px'>" +
                "<h3>前端页面缺失</h3><p>未找到 app/resources/web/index.html</p></body>");
    }

    /// <summary>应用悬浮球自定义图案（assets.upload/reset 后调用；path=null 还原默认）。</summary>
    public void UpdateBallImage(string? path)
        => _ball.Dispatcher.Invoke(() => _ball.SetImage(path));

    /// <summary>
    /// 主面板**紧贴悬浮球**展开（§8.2.5），方位由悬浮球自身所在象限决定，而非贴屏幕角：
    /// 球在右半屏 → 面板在球的左侧；球在上半屏 → 面板在球的下方（其余镜像）。
    /// </summary>
    public void PlaceBesideBall(double? sizeW = null, double? sizeH = null)
    {
        var wa = SystemParameters.WorkArea;
        const double gap = 12;
        var bw = _ball.Width;
        var bh = _ball.Height;

        var toLeft = _ball.Left + bw / 2 >= wa.Left + wa.Width / 2;   // 球在右 → 面板贴球左侧
        var below = _ball.Top + bh / 2 <= wa.Top + wa.Height / 2;     // 球在上 → 面板贴球下方

        var w = sizeW ?? (ActualWidth > 0 ? ActualWidth : Width);
        var h = sizeH ?? (ActualHeight > 0 ? ActualHeight : Height);

        var x = toLeft ? _ball.Left - w - gap : _ball.Left + bw + gap;
        var y = below ? _ball.Top + bh + gap : _ball.Top - h - gap;

        // 某一侧空间不足时翻到另一侧，避免压住悬浮球
        if (x < wa.Left || x + w > wa.Right)
            x = toLeft ? _ball.Left + bw + gap : _ball.Left - w - gap;
        if (y < wa.Top || y + h > wa.Bottom)
            y = below ? _ball.Top - h - gap : _ball.Top + bh + gap;

        Left = Math.Min(Math.Max(x, wa.Left), Math.Max(wa.Left, wa.Right - w));
        Top = Math.Min(Math.Max(y, wa.Top), Math.Max(wa.Top, wa.Bottom - h));
    }

    /// <summary>
    /// 显示主面板（§8.2.7）：先显式还原最小化状态，再定位显示。
    /// 只设 WindowState=Normal 不够——窗口曾最小化到任务栏时，WPF 的 Show() 不会还原，
    /// 必须直接对 HWND 调 SW_RESTORE。
    /// </summary>
    public void ShowPanel()
    {
        // 隐藏/最小化后再次打开 → 固定回默认尺寸，不沿用上次手动拖动的大小（§8.2.6）
        var reopening = !IsVisible || WindowState == WindowState.Minimized;
        if (!IsVisible) Show();

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        if (reopening)
        {
            Width = _defaultWidth;
            Height = _defaultHeight;
            UpdateLayout();
        }

        PlaceBesideBall(reopening ? _defaultWidth : null, reopening ? _defaultHeight : null);
        Show();
        Activate();
    }

    /// <summary>
    /// 前端边缘/角命中后交给系统原生缩放（§8.2.6）。
    /// WebView2 子窗口铺满客户区，WindowChrome 的左右下边框收不到命中，
    /// 因此由 HTML 命中层发起，这里以 HT 码进入系统缩放循环，四边/四角都能拖拽。
    /// </summary>
    public void BeginResize(string edge)
    {
        var ht = edge switch
        {
            "left" => HTLEFT,
            "right" => HTRIGHT,
            "bottom" => HTBOTTOM,
            "bottom-left" => HTBOTTOMLEFT,
            "bottom-right" => HTBOTTOMRIGHT,
            _ => 0,
        };
        if (ht == 0) return;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(ht), IntPtr.Zero);
    }

    // ---------- 悬浮球右键菜单（§8.3）：展开主面板并转发前端 ----------

    public void MenuRefresh() { ShowPanel(); _bridge?.SendEvent("menu.action", new JsonObject { ["name"] = "refresh" }); }
    public void MenuOpenSettings() { ShowPanel(); _bridge?.SendEvent("menu.action", new JsonObject { ["name"] = "settings" }); }
    public void MenuLogout() { ShowPanel(); _bridge?.SendEvent("menu.action", new JsonObject { ["name"] = "logout" }); }

    public void NotifyBallBadge(int unfinished)
    {
        // P0：徽标先体现在悬浮球 Tooltip；数字角标后续做自绘
        _ball.Dispatcher.Invoke(() =>
            _ball.Ball.ToolTip = unfinished > 0 ? $"学习通悬浮球（{unfinished} 项未完成）" : "学习通悬浮球");
    }

    /// <summary>关闭按钮 = 隐藏到悬浮球，不退出软件；退出走悬浮球右键菜单。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
