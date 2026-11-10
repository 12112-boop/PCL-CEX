using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

/// <summary>
///     首页顶部的「AI 崩溃分析」卡片：展示最近一次崩溃的本地规则分析结果，
///     并可调用已配置的 AI 接口做更深入诊断（混合模式：本地优先，AI 可选）。
/// </summary>
public class CrashAnalysisCard : MyCard
{
    private CancellationTokenSource _cancellation;

    public CrashAnalysisCard()
    {
        Title = Lang.Text("CrashAgent.Title");
        Margin = new Thickness(0, 0, 0, 15);
        var panel = new StackPanel { Margin = new Thickness(25, 40, 25, 20) };

        // 元信息行：检测时间 + 版本
        var metaGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var meta = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 0)
        };
        Grid.SetColumn(meta, 0);
        metaGrid.Children.Add(meta);
        // 本地规则分析结果
        var localText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };
        panel.Children.Add(metaGrid);
        panel.Children.Add(localText);

        // AI 深入分析按钮
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var btnAi = new MyButton
        {
            Text = Lang.Text("CrashAgent.Analyze"),
            MinWidth = 110,
            Height = 30,
            Margin = new Thickness(0, 0, 8, 0)
        };
        btnAi.Click += BtnAiAnalyze_Click;
        var btnDismiss = new MyButton
        {
            Text = Lang.Text("CrashAgent.Dismiss"),
            MinWidth = 72,
            Height = 30
        };
        btnDismiss.Click += BtnDismiss_Click;
        btnPanel.Children.Add(btnAi);
        btnPanel.Children.Add(btnDismiss);
        panel.Children.Add(btnPanel);

        // AI 分析结果（默认隐藏）
        var aiText = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Visibility = Visibility.Collapsed
        };
        panel.Children.Add(aiText);

        // 把内部控件名存到字段供刷新用
        _meta = meta;
        _localText = localText;
        _aiText = aiText;
        _btnAi = btnAi;

        Children.Add(panel);
    }

    private TextBlock _meta;
    private TextBlock _localText;
    private TextBlock _aiText;
    private MyButton _btnAi;

    /// <summary>
    ///     从 CrashStore 加载最近崩溃数据并刷新卡片文本。
    /// </summary>
    public void RefreshCrashCard()
    {
        try
        {
            var crash = CrashStore.Load();
            if (crash is null)
            {
                Visibility = Visibility.Collapsed;
                return;
            }
            Visibility = Visibility.Visible;
            _meta.Text = Lang.Text("CrashAgent.Meta", crash.DetectedAt, crash.Version);
            _localText.Text = crash.Reason;
            _aiText.Visibility = Visibility.Collapsed;
            _btnAi.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "AI 崩溃分析卡片刷新失败", ModBase.LogLevel.Hint);
        }
    }

    private void BtnAiAnalyze_Click(object sender, MouseButtonEventArgs e)
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
            ModMain.frmMain?.PageChange(FormMain.PageType.Tools, FormMain.PageSubType.ToolsAiChat);
            return;
        }

        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        _aiText.Visibility = Visibility.Visible;
        _aiText.Text = Lang.Text("CrashAgent.Analyzing");
        _btnAi.IsEnabled = false;

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
                        ModBase.RunInUi(() => _aiText.Text = snapshot);
                    },
                    token);
                ModBase.RunInUi(() =>
                {
                    _aiText.Text = text;
                    _btnAi.IsEnabled = true;
                });
            }
            catch (OperationCanceledException)
            {
                ModBase.RunInUi(() => _btnAi.IsEnabled = true);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "AI 崩溃深入分析失败", ModBase.LogLevel.Hint);
                ModBase.RunInUi(() =>
                {
                    _aiText.Text = Lang.Text("CrashAgent.Error.AnalyzeFailed", ex.Message);
                    _btnAi.IsEnabled = true;
                });
            }
        }, "Crash AI Analyze");
    }

    private void BtnDismiss_Click(object sender, MouseButtonEventArgs e)
    {
        CrashStore.Clear();
        Visibility = Visibility.Collapsed;
        _cancellation?.Cancel();
        _cancellation = null;
    }
}
