using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.Ai;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

public partial class PageToolsAiChat
{
    /// <summary>
    ///     当前会话的历史消息。
    /// </summary>
    private readonly List<AiChatMessage> _history = new();

    /// <summary>
    ///     正在进行的请求的取消源。
    /// </summary>
    private CancellationTokenSource _cancellation;

    /// <summary>
    ///     是否正在等待 AI 回复。
    /// </summary>
    private bool _isGenerating;

    public PageToolsAiChat()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    /// <summary>
    ///     本地 AI 服务商预设：索引 -> (接口地址, 显示名 Key)。
    /// </summary>
    private static readonly (string BaseUrl, string NameKey)[] LocalProviders =
    [
        ("http://localhost:11434/v1", "Tools.AiChat.Provider.Ollama"),
        ("http://localhost:1234/v1", "Tools.AiChat.Provider.LmStudio")
    ];

    /// <summary>
    ///     切回云端服务时恢复的接口地址。
    /// </summary>
    private string _cloudBaseUrl = "https://api.openai.com/v1";

    /// <summary>
    ///     刷新页面：加载已保存的接口设置。
    /// </summary>
    public void Reload()
    {
        try
        {
            // 服务商下拉框
            if (CmbProvider.Items.Count == 0)
            {
                CmbProvider.Items.Add(Lang.Text("Tools.AiChat.Provider.Cloud"));
                foreach (var (_, nameKey) in LocalProviders)
                    CmbProvider.Items.Add(Lang.Text(nameKey));
            }
            var provider = Config.Tool.AiChatProvider;
            if (provider < 0 || provider >= CmbProvider.Items.Count) provider = 0;
            CmbProvider.SelectedIndex = provider;

            TextBaseUrl.Text = Config.Tool.AiChatBaseUrl ?? string.Empty;
            // 记录云端地址，便于从本地服务切回时恢复
            if (provider == 0 && !string.IsNullOrWhiteSpace(TextBaseUrl.Text))
                _cloudBaseUrl = TextBaseUrl.Text.Trim();
            TextApiKey.Text = Config.Tool.AiChatApiKey ?? string.Empty;
            TextModel.Text = Config.Tool.AiChatModel ?? string.Empty;
            TextSystemPrompt.Text = Config.Tool.AiChatSystemPrompt ?? string.Empty;
            ApplyProviderState();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "AI 聊天：加载接口设置失败", ModBase.LogLevel.Hint);
        }
    }

    /// <summary>
    ///     根据选择的服务商更新界面状态：本地服务自动填充地址且无需 API Key。
    /// </summary>
    private void ApplyProviderState()
    {
        var provider = CmbProvider.SelectedIndex;
        var isLocal = provider > 0 && provider <= LocalProviders.Length;
        TextApiKey.IsEnabled = !isLocal;
        TextApiKey.HintText = isLocal
            ? Lang.Text("Tools.AiChat.Settings.LocalNoKey")
            : Lang.Text("Tools.AiChat.Settings.ApiKeyHint");
        BtnGetModels.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    ///     切换服务商：本地服务自动填充对应本地地址，切回云端时恢复此前填写的云端地址。
    /// </summary>
    private void CmbProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        try
        {
            var provider = CmbProvider.SelectedIndex;
            Config.Tool.AiChatProvider = provider;
            if (provider > 0 && provider <= LocalProviders.Length)
            {
                // 本地服务：始终使用该服务商的预设地址
                TextBaseUrl.Text = LocalProviders[provider - 1].BaseUrl;
            }
            else if (!string.IsNullOrEmpty(_cloudBaseUrl))
            {
                // 切回云端：恢复此前保存的云端地址
                TextBaseUrl.Text = _cloudBaseUrl;
            }
            ApplyProviderState();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "AI 聊天：保存服务商设置失败", ModBase.LogLevel.Hint);
        }
    }

    /// <summary>
    ///     是否为本地 AI 模式（无需 API Key）。
    /// </summary>
    private bool IsLocalProvider()
    {
        var provider = CmbProvider.SelectedIndex;
        return provider > 0 && provider <= LocalProviders.Length;
    }

    /// <summary>
    ///     获取本地服务的模型列表并让用户选择。
    /// </summary>
    private void BtnGetModels_Click(object sender, MouseButtonEventArgs e)
    {
        // 注意：必须在后台线程开始前读取 UI 状态，避免跨线程访问控件
        var baseUrl = TextBaseUrl.Text?.Trim() ?? string.Empty;
        var apiKey = TextApiKey.Text?.Trim() ?? string.Empty;
        var isLocal = IsLocalProvider();
        if (string.IsNullOrEmpty(baseUrl))
        {
            HintService.Hint(Lang.Text("Tools.AiChat.Error.NotConfigured"), HintType.Error);
            return;
        }

        BtnGetModels.IsEnabled = false;
        BtnGetModels.Text = Lang.Text("Tools.AiChat.Status.Testing");
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var models = ModAiChat.ListModelsAsync(baseUrl, apiKey)
                    .GetAwaiter().GetResult();
                if (models.Count == 0)
                {
                    HintService.Hint(Lang.Text("Tools.AiChat.Models.None"), HintType.Warning);
                    return;
                }
                string selected = null;
                ModBase.RunInUiWait(() =>
                {
                    var controls = models.Select(m => (IMyRadio)new MyRadioBox { Text = m }).ToList();
                    var index = ModMain.MyMsgBoxSelect(controls, Lang.Text("Tools.AiChat.Models.Title"));
                    if (index is >= 0 and < 100000) selected = models[index.Value];
                });
                if (!string.IsNullOrEmpty(selected))
                {
                    ModBase.RunInUi(() => { TextModel.Text = selected; });
                    HintService.Hint(Lang.Text("Tools.AiChat.Models.Selected", selected));
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "AI 聊天：获取模型列表失败", ModBase.LogLevel.Hint);
                var message = Lang.Text("Tools.AiChat.Models.Failed", ex.Message);
                if (isLocal) message += Environment.NewLine + Lang.Text("Tools.AiChat.Error.LocalNotRunning");
                HintService.Hint(message, HintType.Error);
            }
            finally
            {
                ModBase.RunInUi(() =>
                {
                    BtnGetModels.IsEnabled = true;
                    BtnGetModels.Text = Lang.Text("Tools.AiChat.Settings.GetModels");
                });
            }
        }, "AI Chat List Models");
    }

    /// <summary>
    ///     保存接口设置到配置（本地存储，Key 加密）。
    /// </summary>
    private void Settings_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        try
        {
            Config.Tool.AiChatBaseUrl = TextBaseUrl.Text?.Trim() ?? string.Empty;
            // 云端模式下同步记录地址，供切回云端时恢复
            if (!IsLocalProvider() && !string.IsNullOrWhiteSpace(TextBaseUrl.Text))
                _cloudBaseUrl = TextBaseUrl.Text.Trim();
            Config.Tool.AiChatApiKey = TextApiKey.Text?.Trim() ?? string.Empty;
            Config.Tool.AiChatModel = TextModel.Text?.Trim() ?? string.Empty;
            Config.Tool.AiChatSystemPrompt = TextSystemPrompt.Text ?? string.Empty;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "AI 聊天：保存接口设置失败", ModBase.LogLevel.Hint);
        }
    }

    /// <summary>
    ///     输入框中按 Enter 发送，Shift+Enter 换行。
    /// </summary>
    private void TextInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) return;
        e.Handled = true;
        SendMessage();
    }

    private void BtnSend_Click(object sender, MouseButtonEventArgs e)
    {
        SendMessage();
    }

    /// <summary>
    ///     发送当前输入的消息。
    /// </summary>
    private void SendMessage()
    {
        if (_isGenerating) return;
        var text = TextInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return;

        // 注意：以下 UI 状态必须在后台线程开始前读取，避免跨线程访问控件
        var apiKey = TextApiKey.Text?.Trim() ?? string.Empty;
        var baseUrl = TextBaseUrl.Text?.Trim() ?? string.Empty;
        var model = TextModel.Text?.Trim() ?? string.Empty;
        var systemPrompt = TextSystemPrompt.Text ?? string.Empty;
        var isLocal = IsLocalProvider();
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(model) ||
            (!isLocal && string.IsNullOrEmpty(apiKey)))
        {
            HintService.Hint(Lang.Text("Tools.AiChat.Error.NotConfigured"), HintType.Error);
            // 自动展开设置卡片方便用户配置
            CardSettings.IsSwapped = false;
            return;
        }

        // 记录用户消息
        _history.Add(new AiChatMessage { Role = "user", Content = text });
        AddBubble(text, true);
        TextInput.Text = string.Empty;
        UpdateEmptyHint();

        // 创建 AI 气泡（流式填充）
        var bubble = AddBubble(string.Empty, false);
        SetGenerating(true);
        var fullResponse = new System.Text.StringBuilder();

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        ModBase.RunInNewThread(async () =>
        {
            try
            {
                await ModAiChat.SendChatAsync(
                    _history,
                    baseUrl,
                    apiKey,
                    model,
                    systemPrompt,
                    delta =>
                    {
                        // 增量文本回到 UI 线程追加
                        ModBase.RunInUi(() =>
                        {
                            fullResponse.Append(delta);
                            bubble.Content.Text += delta;
                            ScrollToBottom();
                        });
                    },
                    token).ConfigureAwait(false);

                ModBase.RunInUiWait(() =>
                {
                    if (fullResponse.Length == 0)
                        bubble.Content.Text = Lang.Text("Tools.AiChat.Error.EmptyResponse");
                    else
                        _history.Add(new AiChatMessage { Role = "assistant", Content = fullResponse.ToString() });
                    SetGenerating(false);
                });
            }
            catch (OperationCanceledException)
            {
                ModBase.RunInUiWait(() =>
                {
                    bubble.Content.Text += Environment.NewLine +
                                           Lang.Text("Tools.AiChat.Status.Stopped");
                    SetGenerating(false);
                });
            }
            catch (AiChatException ex)
            {
                ModBase.Log(ex, "AI 聊天：请求失败", ModBase.LogLevel.Hint);
                ModBase.RunInUiWait(() =>
                {
                    bubble.Content.Text = Lang.Text("Tools.AiChat.Error.RequestFailed", ex.Message);
                    if (isLocal)
                        bubble.Content.Text += Environment.NewLine +
                                               Lang.Text("Tools.AiChat.Error.LocalNotRunning");
                    // 请求失败时移除该次用户消息，避免污染历史
                    RemoveLastUserMessage();
                    SetGenerating(false);
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "AI 聊天：未知错误", ModBase.LogLevel.Hint);
                ModBase.RunInUiWait(() =>
                {
                    bubble.Content.Text = Lang.Text("Tools.AiChat.Error.RequestFailed", ex.Message);
                    if (isLocal)
                        bubble.Content.Text += Environment.NewLine +
                                               Lang.Text("Tools.AiChat.Error.LocalNotRunning");
                    RemoveLastUserMessage();
                    SetGenerating(false);
                });
            }
        }, "AI Chat Send");
    }

    /// <summary>
    ///     移除最后一条用户消息（请求失败时调用）。
    /// </summary>
    private void RemoveLastUserMessage()
    {
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Role != "user") continue;
            _history.RemoveAt(i);
            break;
        }
    }

    private void BtnStop_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            _cancellation?.Cancel();
        }
        catch
        {
            // 忽略取消失败
        }
    }

    private void BtnClear_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isGenerating)
        {
            HintService.Hint(Lang.Text("Tools.AiChat.Error.Generating"), HintType.Warning);
            return;
        }
        if (_history.Count == 0 && PanMessages.Children.Count == 0) return;
        if (ModMain.MyMsgBox(
                Lang.Text("Tools.AiChat.Clear.Confirm"),
                Lang.Text("Tools.AiChat.Clear.Title"),
                Lang.Text("Common.Action.Continue"),
                Lang.Text("Common.Action.Cancel")) == 2)
            return;
        _history.Clear();
        PanMessages.Children.Clear();
        UpdateEmptyHint();
        HintService.Hint(Lang.Text("Tools.AiChat.Clear.Done"));
    }

    /// <summary>
    ///     测试接口连通性。
    /// </summary>
    private void BtnTestConnection_Click(object sender, MouseButtonEventArgs e)
    {
        // 注意：必须在后台线程开始前读取 UI 状态，避免跨线程访问控件
        var apiKey = TextApiKey.Text?.Trim() ?? string.Empty;
        var baseUrl = TextBaseUrl.Text?.Trim() ?? string.Empty;
        var model = TextModel.Text?.Trim() ?? string.Empty;
        var isLocal = IsLocalProvider();
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(model) ||
            (!isLocal && string.IsNullOrEmpty(apiKey)))
        {
            HintService.Hint(Lang.Text("Tools.AiChat.Error.NotConfigured"), HintType.Error);
            return;
        }

        BtnTestConnection.IsEnabled = false;
        BtnTestConnection.Text = Lang.Text("Tools.AiChat.Status.Testing");
        ModBase.RunInNewThread(async () =>
        {
            try
            {
                await ModAiChat.TestConnectionAsync(baseUrl, apiKey, model).ConfigureAwait(false);
                HintService.Hint(Lang.Text("Tools.AiChat.Test.Success"), HintType.Success);
            }
            catch (OperationCanceledException)
            {
                // 忽略
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "AI 聊天：连接测试失败", ModBase.LogLevel.Hint);
                var message = Lang.Text("Tools.AiChat.Test.Failed", ex.Message);
                if (isLocal) message += Environment.NewLine + Lang.Text("Tools.AiChat.Error.LocalNotRunning");
                HintService.Hint(message, HintType.Error);
            }
            finally
            {
                ModBase.RunInUi(() =>
                {
                    BtnTestConnection.IsEnabled = true;
                    BtnTestConnection.Text = Lang.Text("Tools.AiChat.Action.TestConnection");
                });
            }
        }, "AI Chat Test");
    }

    /// <summary>
    ///     添加一条聊天气泡。isUser 为 true 时右对齐显示为用户消息。
    /// </summary>
    private ChatBubble AddBubble(string text, bool isUser)
    {
        var bubble = new ChatBubble
        {
            IsUser = isUser,
            Margin = new Thickness(0, 0, 0, 10)
        };
        bubble.SetText(text);
        PanMessages.Children.Add(bubble);
        ScrollToBottom();
        return bubble;
    }

    private void ScrollToBottom()
    {
        PanChatScroller.ScrollToEnd();
    }

    private void UpdateEmptyHint()
    {
        LabEmpty.Visibility = PanMessages.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    ///     设置生成中状态。
    /// </summary>
    private void SetGenerating(bool generating)
    {
        _isGenerating = generating;
        BtnSend.Visibility = generating ? Visibility.Collapsed : Visibility.Visible;
        BtnStop.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;
        TextInput.IsEnabled = !generating;
        if (!generating) _cancellation?.Dispose();
        _cancellation = null;
    }
}

/// <summary>
///     聊天气泡控件：用户消息右对齐，AI 消息左对齐。
/// </summary>
public class ChatBubble : Border
{
    public bool IsUser { get; set; }

    public TextBlock Content { get; } = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(12, 8, 12, 8),
        FontSize = 13
    };

    public ChatBubble()
    {
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(0);
        MaxWidth = 560;
        HorizontalAlignment = IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Child = Content;
        ApplyTheme();
        Loaded += (_, _) => ApplyTheme();
    }

    public void SetText(string text)
    {
        Content.Text = text;
    }

    private void ApplyTheme()
    {
        if (IsUser)
        {
            SetResourceReference(BackgroundProperty, "ColorBrush4");
            Content.Foreground = Brushes.White;
        }
        else
        {
            SetResourceReference(BackgroundProperty, "ColorBrush7");
            Content.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
        }
    }
}
