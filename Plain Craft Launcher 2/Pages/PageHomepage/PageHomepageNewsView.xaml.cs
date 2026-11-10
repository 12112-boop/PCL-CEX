using System;
using System.Threading;
using System.Windows;
using PCL.Core.App.Localization;
using PCL.Core.UI;
using PCL.Core.ViewModel.Homepage;

namespace PCL;

public partial class PageHomepageNewsView : MyPageRight
{
    private CancellationTokenSource _crashAnalysisCancellation;

    public PageHomepageNewsView()
    {
        InitializeComponent();
        DataContext = new NewsViewModel();
        Loaded += (_, _) => RefreshCrashCard();
    }

    /// <summary>
    ///     首页「AI 崩溃分析」卡片：读取最近一次崩溃摘要并展示本地规则分析结果。
    /// </summary>
    public void RefreshCrashCard()
    {
        try
        {
            var crash = CrashStore.Load();
            if (crash is null)
            {
                CardCrashAnalysis.Visibility = Visibility.Collapsed;
                return;
            }

            CardCrashAnalysis.Visibility = Visibility.Visible;
            TxtCrashMeta.Text = Lang.Text("CrashAgent.Meta", crash.DetectedAt, crash.Version);
            TxtCrashLocal.Text = crash.Reason;
            TxtCrashAi.Visibility = Visibility.Collapsed;
            BtnAiAnalyze.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "AI 崩溃分析卡片加载失败", ModBase.LogLevel.Hint);
        }
    }

    /// <summary>
    ///     调用 AI 对最近崩溃做深入分析（流式输出）。
    /// </summary>
    private void BtnAiAnalyze_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var crash = CrashStore.Load();
        if (crash is null)
        {
            HintService.Hint(Lang.Text("CrashAgent.Error.NoCrash"), HintType.Error);
            return;
        }

        if (!CrashAgentService.IsConfigured())
        {
            HintService.Hint(Lang.Text("CrashAgent.Error.NotConfigured"), HintType.Warning);
            ModMain.frmMain.PageChange(FormMain.PageType.Tools, FormMain.PageSubType.ToolsAiChat);
            return;
        }

        _crashAnalysisCancellation?.Cancel();
        _crashAnalysisCancellation = new CancellationTokenSource();
        var token = _crashAnalysisCancellation.Token;

        TxtCrashAi.Visibility = Visibility.Visible;
        TxtCrashAi.Text = Lang.Text("CrashAgent.Analyzing");
        BtnAiAnalyze.IsEnabled = false;

        ModBase.RunInNewThread(() =>
        {
            try
            {
                var full = new System.Text.StringBuilder();
                var text = CrashAgentService.Analyze(
                    crash,
                    delta =>
                    {
                        full.Append(delta);
                        var snapshot = full.ToString();
                        ModBase.RunInUi(() => TxtCrashAi.Text = snapshot);
                    },
                    token);
                ModBase.RunInUi(() =>
                {
                    TxtCrashAi.Text = text;
                    BtnAiAnalyze.IsEnabled = true;
                });
            }
            catch (OperationCanceledException)
            {
                ModBase.RunInUi(() => BtnAiAnalyze.IsEnabled = true);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "AI 崩溃深入分析失败", ModBase.LogLevel.Hint);
                ModBase.RunInUi(() =>
                {
                    TxtCrashAi.Text = Lang.Text("CrashAgent.Error.AnalyzeFailed", ex.Message);
                    BtnAiAnalyze.IsEnabled = true;
                });
            }
        }, "Crash AI Analyze");
    }

    /// <summary>
    ///     关闭崩溃分析卡片（清除持久化的最近崩溃记录）。
    /// </summary>
    private void BtnDismissCrash_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        CrashStore.Clear();
        CardCrashAnalysis.Visibility = Visibility.Collapsed;
        _crashAnalysisCancellation?.Cancel();
        _crashAnalysisCancellation = null;
    }
}
