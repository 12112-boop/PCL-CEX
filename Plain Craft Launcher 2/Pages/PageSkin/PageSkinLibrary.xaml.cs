using System;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

public partial class PageSkinLibrary : MyPageRight
{
    private bool _webViewInitialized;
    private bool _webReady;

    public PageSkinLibrary()
    {
        InitializeComponent();
        Loaded += PageSkinLibrary_Loaded;
    }

    private async void PageSkinLibrary_Loaded(object sender, RoutedEventArgs e)
    {
        if (_webViewInitialized)
        {
            if (_webReady)
                NavigateToLittleskin();
            return;
        }
        _webViewInitialized = true;
        try
        {
            var userDataFolder = Path.Combine(ModBase.pathTemp, "Cache", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
            await WebView.EnsureCoreWebView2Async(env);
            _webReady = true;
            var core = WebView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.IsStatusBarEnabled = false;

            // 拦截新窗口请求：内部跳转，而非外部浏览器
            core.NewWindowRequested += (s, args) =>
            {
                args.Handled = true;
                core.Navigate(args.Uri);
                ModBase.Log("[Skin] 新窗口请求已拦截并转为内部跳转：" + args.Uri);
            };

            // 完全接管下载请求：取消 WebView2 默认下载行为（避免弹外部浏览器/下载对话框），
            // 改用我们自己的 HttpClient 直接下载到「我的皮肤」目录
            core.DownloadStarting += (s, args) =>
            {
                var op = args.DownloadOperation;
                var url = op.Uri;
                var fileName = Path.GetFileName(new Uri(url).LocalPath);
                if (string.IsNullOrEmpty(fileName))
                    fileName = Path.GetFileName(op.ContentDisposition);
                if (string.IsNullOrEmpty(fileName))
                    fileName = ModBase.GetHash(url).ToString() + ".png";
                if (!fileName.EndsWithF(".png", true)) fileName += ".png";
                ModBase.Log("[Skin] 拦截下载请求，取消 WebView2 默认行为：" + url);
                // 标记为已处理并取消，WebView2 不再执行默认下载
                args.Handled = true;
                args.Cancel = true;
                // 异步下载并保存到「我的皮肤」目录
                _ = DownloadAndStoreSkinAsync(url, fileName);
            };

            NavigateToLittleskin();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "WebView2 初始化失败", ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Skin.Library.WebViewError", ex.Message));
            HintStatus.Visibility = Visibility.Visible;
            HintStatus.Text = Lang.Text("Skin.Library.WebViewError", ex.Message);
            HintService.Hint(Lang.Text("Skin.Library.WebViewError", ex.Message), HintType.Error);
        }
    }

    private void NavigateToLittleskin()
    {
        if (_webReady)
            WebView.CoreWebView2.Navigate("https://littleskin.cn/skinlib");
    }

    public void Reload()
    {
        NavigateToLittleskin();
    }

    private void BtnBack_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_webReady && WebView.CoreWebView2.CanGoBack)
            WebView.CoreWebView2.GoBack();
    }

    private void BtnForward_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_webReady && WebView.CoreWebView2.CanGoForward)
            WebView.CoreWebView2.GoForward();
    }

    private void BtnRefresh_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_webReady)
            WebView.CoreWebView2.Reload();
    }

    private void BtnOpen_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ModBase.OpenWebsite("https://littleskin.cn/skinlib");
    }

    private async Task DownloadAndStoreSkinAsync(string url, string fileName)
    {
        try
        {
            var stored = await Task.Run(() =>
                ModSkinStore.DownloadSkinFromUrl(url, fileName));
            ModBase.Log("[Skin] 皮肤已保存到「我的皮肤」：" + stored.LocalFile);
            ModBase.RunInUi(() =>
            {
                HintService.Hint(Lang.Text("Skin.Library.DownloadSuccess", stored.Name), HintType.Success);
                // 刷新「我的皮肤」列表
                ModMain.frmSkinMine?.Reload();
            });
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "拦截下载失败：" + url, ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Skin.Library.DownloadError", ex.Message));
            ModBase.RunInUi(() =>
                HintService.Hint(Lang.Text("Skin.Library.DownloadError", ex.Message), HintType.Error));
        }
    }
}
