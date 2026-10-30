using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace CxBall.Shell;

/// <summary>
/// 内嵌登录窗（方案 §3.1）：加载 v8.chaoxing.com，用户手动登录（滑块/扫码零逆向）。
/// 成功判定：.chaoxing.com 域 Cookie 中出现 _uid 与 vc3。成功后抽取全域 Cookie 回调。
/// 与主面板共用全局 Profile（data/browser-profile），便于“记住登录”。
/// </summary>
public partial class LoginWindow : Window
{
    public const string LoginUrl = "https://v8.chaoxing.com/";

    /// <summary>登录成功回调：参数 = (uid, cookiesJsonArray)。</summary>
    public event Action<string, JsonArray>? LoggedIn;

    /// <summary>用户已提交登录（登录页发生顶层跳转）→ 通知前端在该位置显示进度条。</summary>
    public event Action? Submitting;

    bool _done;
    bool _loginPageLoaded;

    public LoginWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await Init();
    }

    async Task Init()
    {
        await Web.EnsureCoreWebView2Async(App.WebEnv);
        // 学习通登录页为定宽双栏布局（约 1300px）：窗口加宽 + 85% 缩放保证整页完整可见
        Web.ZoomFactor = 0.85;
        // 登录页首次加载完成后，任何顶层跳转都视为「用户已提交登录」（iframe 跳转不会触发本事件）
        Web.CoreWebView2.NavigationStarting += (_, _) =>
        {
            if (_loginPageLoaded && !_done) Submitting?.Invoke();
        };
        Web.CoreWebView2.NavigationCompleted += async (_, _) =>
        {
            _loginPageLoaded = true;
            await TryExtract();
        };
        Web.CoreWebView2.Navigate(LoginUrl);
    }

    async Task TryExtract()
    {
        if (_done || Web.CoreWebView2 == null) return;
        try
        {
            var all = await Web.CoreWebView2.CookieManager.GetCookiesAsync(null!);
            var cookies = new JsonArray();
            string? uid = null;
            bool hasVc3 = false;
            foreach (var c in all)
            {
                if (!c.Domain.Contains("chaoxing.com")) continue;
                cookies.Add(new JsonObject
                {
                    ["name"] = c.Name,
                    ["value"] = c.Value,
                    ["domain"] = c.Domain,
                    ["path"] = c.Path,
                });
                if (c.Name == "_uid" && !string.IsNullOrEmpty(c.Value)) uid = c.Value;
                if (c.Name == "vc3") hasVc3 = true;
            }
            if (uid == null || !hasVc3) return;

            _done = true;
            LoggedIn?.Invoke(uid, cookies);
            Close();
        }
        catch { /* 导航中途取 Cookie 失败忽略，下次导航再试 */ }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 用户主动关窗 = 登录失败/取消：不产生登录事件，不占冷却（方案 §3.1）
        base.OnClosing(e);
    }
}
