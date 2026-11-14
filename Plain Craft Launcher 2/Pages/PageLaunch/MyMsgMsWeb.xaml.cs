using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI.Controls;
using PCL.Core.Utils;
using PCL.Core.IO.Net.Http;
using PCL.Core.Logging;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.IdentityModel.OAuth;
using PCL.Core.Minecraft.Profile.Authentication;
using JsonObject = System.Text.Json.Nodes.JsonObject;

namespace PCL;

/// <summary>
///     微软单窗口（内嵌 WebView）登录弹窗：在启动器内部完成微软账号登录，
///     用户无需离开启动器窗口，也无需手动复制设备码。
///     注意：WebView2 是 HWND 子窗口，WPF 变换（RenderTransform）对它无效，
///     因此滑入/滑出动画期间必须先隐藏 WebView，动画结束后再显示并初始化。
/// </summary>
public partial class MyMsgMsWeb
{
    private readonly ModMain.MyMsgBoxConverter myConverter;
    private readonly int uuid = ModBase.GetUuid();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly JsonObject data;
    private readonly DeviceCodeAuthenticationContext _context;
    private string _website = "";
    private bool _webStarted;

    public MyMsgMsWeb(ModMain.MyMsgBoxConverter converter)
    {
        myConverter = converter;
        try
        {
            InitializeComponent();
            BtnOpen.Name += ModBase.GetUuid();
            BtnCopy.Name += ModBase.GetUuid();
            BtnCancel.Name += ModBase.GetUuid();
            ShapeLine.StrokeThickness = ModBase.GetWPFSize(1d);

            // Content 直接传 DeviceCodeAuthenticationContext 对象（含 Func，不能序列化）
            _context = converter.Content as DeviceCodeAuthenticationContext
                       ?? throw new InvalidDataException("微软内嵌登录数据无效");
            data = System.Text.Json.JsonSerializer.SerializeToNode(_context.Data, JsonCompat.SerializerOptions)!.AsObject();
            _website = _ContextUrl();
            Loaded += Load;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex,
                Lang.Text("Launch.Account.LoginDialog.MsWeb.InitError"),
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Launch.Account.LoginDialog.MsWeb.InitError"));
            // 初始化失败：立刻把异常交还给登录流程，避免调用方永久等待
            var error = new InvalidDataException(Lang.Text("Launch.Account.LoginDialog.MsWeb.InitError"), ex);
            converter.IsExited = true;
            converter.Result = error;
            converter.CompletionHandler?.Invoke(error);
            throw;
        }
    }

    private void Load(object sender, EventArgs e)
    {
        try
        {
            // 滑入动画：WebView 此时仍为 Collapsed，不会出现 HWND 与弹窗错位
            Opacity = 0d;
            TransformPos.Y = 50d;
            ModAnimation.AniStart(
                ModAnimation.AaColor(ModMain.frmMain.PanMsgBackground, BlurBorder.BackgroundProperty,
                    new ModBase.MyColor(90d, 0d, 0d, 0d) - ModMain.frmMain.PanMsgBackground.Background, 200),
                "PanMsgBackground Background");
            ModAnimation.AniStart(
                new[]
                {
                    ModAnimation.AaOpacity(this, 1d, 160, 30),
                    ModAnimation.AaDouble(i => TransformPos.Y += (double)i, -TransformPos.Y, 260, 30,
                        new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
                    // 动画结束后再显示并初始化 WebView
                    ModAnimation.AaCode(() => _ = StartWebAsync(), 290)
                }, "MyMsgMsWeb " + uuid);

            ModBase.Log($"[Control] 微软内嵌登录弹窗：{LabTitle.Text}\r\n{_website}");

            // 显示设备码，并自动复制到剪贴板
            if (data["user_code"] is System.Text.Json.Nodes.JsonValue uc)
            {
                var code = uc.ToString();
                LabCode.Text = Lang.Text("Launch.Account.LoginDialog.MsWeb.CodeCopied", code);
                ModBase.ClipboardSet(code, showSuccessHint: false);
            }
            else
            {
                LabCode.Text = Lang.Text("Launch.Account.LoginDialog.MsWeb.CodeMissing");
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex,
                Lang.Text("Launch.Account.LoginDialog.Error.Load"),
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Launch.Account.LoginDialog.Error.Load"));
        }
    }

    private string _ContextUrl()
    {
        // 优先使用带预填设备码的完整链接，用户登录后无需再手动输入
        if (data["verification_uri_complete"] is System.Text.Json.Nodes.JsonValue v) return v.ToString();
        if (data["verification_uri"] is System.Text.Json.Nodes.JsonValue v2) return v2.ToString();
        return "https://www.microsoft.com/link";
    }

    private async Task StartWebAsync()
    {
        if (_webStarted || myConverter.IsExited) return;
        _webStarted = true;
        try
        {
            // 显示 WebView（在动画结束后，避免变换错位）
            WebView.Visibility = System.Windows.Visibility.Visible;

            // 1. 初始化 WebView2（与皮肤页共用缓存目录）
            var userDataFolder = Path.Combine(ModBase.pathTemp, "Cache", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
            await WebView.EnsureCoreWebView2Async(env);

            var core = WebView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.IsStatusBarEnabled = false;

            // 新窗口请求全部拦截为内部导航，避免跳出启动器
            core.NewWindowRequested += (s, args) =>
            {
                args.Handled = true;
                core.Navigate(args.Uri);
            };

            Loading.Visibility = System.Windows.Visibility.Collapsed;
            core.Navigate(_website);
            ModBase.Log("[MsLogin] 内嵌登录页已打开：" + _website);

            // 2. 轮询设备码状态（成功即关闭弹窗）
            await PollAsync();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "微软内嵌登录初始化/轮询失败");
            Finished(ex);
        }
    }

    private async Task PollAsync()
    {
        var token = _cancellation.Token;
        var device = _context.Data;
        LabStatus.Text = Lang.Text("Launch.Account.LoginDialog.MsWeb.Waiting");
        PanStatus.Visibility = System.Windows.Visibility.Visible;
        var delayTime = TimeSpan.FromSeconds(device.Interval ?? 5);
        var unknownFailureCount = 0;
        while (!myConverter.IsExited && !token.IsCancellationRequested)
        {
            try
            {
                var resultJson = await _context.PollAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (resultJson is { IsError: true })
                {
                    switch (resultJson.Error)
                    {
                        case "authorization_pending":
                            await Task.Delay(delayTime, token).ConfigureAwait(false);
                            continue;
                        case "slow_down":
                            delayTime += TimeSpan.FromSeconds(5);
                            await Task.Delay(delayTime, token).ConfigureAwait(false);
                            continue;
                        case "expired_token":
                            Finished(new IdentityModelAuthenticationException(
                                resultJson.Error, resultJson.ErrorDescription ?? "登录已过期，请重试。"));
                            return;
                        case "access_denied":
                        case "authorization_declined":
                            Finished(new IdentityModelAuthenticationException(
                                resultJson.Error, resultJson.ErrorDescription ?? "用户取消了登录。"));
                            return;
                        default:
                            throw new IdentityModelAuthenticationException(
                                resultJson.Error ?? "invalid_response",
                                resultJson.ErrorDescription ?? "无法获取登录结果");
                    }
                }

                if (resultJson is null) throw new InvalidDataException("微软令牌返回为空");
                LogWrapper.Info("Profile", $"令牌过期时间：{resultJson.ExpiresIn} 秒");
                HintService.Hint(Lang.Text("Launch.Account.LoginDialog.Success"), HintType.Success);
                if (myConverter.LoginResultHandler is not null)
                {
                    await myConverter.LoginResultHandler(resultJson, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                Finished(new[] { resultJson.AccessToken ?? string.Empty, resultJson.RefreshToken ?? string.Empty });
                return;
            }
            catch (OperationCanceledException) when (myConverter.IsExited)
            {
                return;
            }
            catch (Exception ex)
            {
                if (unknownFailureCount <= 2)
                {
                    unknownFailureCount += 1;
                    ModBase.Log(ex, $"微软内嵌登录轮询第 {unknownFailureCount} 次失败");
                    try { await Task.Delay(2000, token); }
                    catch (OperationCanceledException) when (myConverter.IsExited)
                    {
                        return;
                    }
                }
                else
                {
                    Finished(new InvalidOperationException(Lang.Text("Launch.Account.LoginDialog.PollingFailed"), ex));
                    return;
                }
            }
        }
    }

    private void Finished(object result)
    {
        if (myConverter.IsExited) return;
        myConverter.IsExited = true;
        _cancellation.Cancel();
        myConverter.Result = result;
        myConverter.CompletionHandler?.Invoke(result);
        ModBase.RunInUi(Close);
        Thread.Sleep(200);
        ModMain.frmMain.ShowWindowToTop();
    }

    private void BtnCopy_Click(object sender, MouseButtonEventArgs e)
    {
        if (data["user_code"] is System.Text.Json.Nodes.JsonValue uc)
        {
            var code = uc.ToString();
            ModBase.ClipboardSet(code);
            LabCode.Text = Lang.Text("Launch.Account.LoginDialog.MsWeb.CodeCopied", code);
        }
    }

    private void BtnOpen_Click(object sender, MouseButtonEventArgs e)
    {
        // 兜底方案：在系统浏览器中完成登录（设备码已复制）
        ModBase.OpenWebsite(_website);
        if (data["user_code"] is System.Text.Json.Nodes.JsonValue uc)
            ModBase.ClipboardSet(uc.ToString(), showSuccessHint: false);
    }

    private void BtnCancel_Click(object sender, MouseButtonEventArgs e)
    {
        Finished(new ThreadInterruptedException());
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        // On Error Resume Next
        if (e.GetPosition(ShapeLine).Y <= 2d)
            ModMain.frmMain.DragMove();
    }

    private void Close()
    {
        // 先隐藏 WebView（HWND 子窗口不受 WPF 变换影响），再做滑出动画
        try { WebView.Visibility = System.Windows.Visibility.Collapsed; }
        catch { /* 忽略 */ }

        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaCode(() =>
            {
                if (!ModMain.WaitingMyMsgBox.Any())
                    ModAnimation.AniStart(ModAnimation.AaColor(ModMain.frmMain.PanMsgBackground,
                        BlurBorder.BackgroundProperty,
                        new ModBase.MyColor(0d, 0d, 0d, 0d) - ModMain.frmMain.PanMsgBackground.Background, 200,
                        ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)));
            }, 30),
            ModAnimation.AaOpacity(this, -Opacity, 100, 20),
            ModAnimation.AaDouble(i => TransformPos.Y += (double)i, 40d - TransformPos.Y,
                140, 20, new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaCode(() => ((Grid)Parent).Children.Remove(this), after: true)
        }, "MyMsgMsWeb " + uuid);
    }
}
