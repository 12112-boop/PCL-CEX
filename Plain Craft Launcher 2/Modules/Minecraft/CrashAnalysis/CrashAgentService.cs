using System;
using System.Collections.Generic;
using System.Threading;
using PCL.Core.Ai;
using PCL.Core.App;
using PCL.Core.App.Localization;

namespace PCL;

/// <summary>
///     AI 崩溃分析 Agent：读取 CrashStore 中的最近崩溃摘要，
///     基于已配置的 AI 接口（工具 → AI 助手）生成更深入的诊断说明。
/// </summary>
public static class CrashAgentService
{
    private const string SystemPrompt =
        "你是 PCL 启动器的崩溃诊断 Agent。用户会提供一次 Minecraft 崩溃的本地规则分析结果。" +
        "请基于这些结果给出更具体、可操作的诊断：1) 崩溃最可能的根因；2) 建议的排查步骤（按优先级）；3) 常见修复方法。" +
        "如果本地分析结果已足够明确，请简短确认并补充一两条提示即可。输出为纯文本，控制在 300 字以内。";

    /// <summary>
    ///     当前是否配置了可用的 AI 接口。
    /// </summary>
    public static bool IsConfigured()
    {
        var baseUrl = Config.Tool.AiChatBaseUrl;
        var model = Config.Tool.AiChatModel;
        var isLocalProvider = Config.Tool.AiChatProvider > 0;
        var apiKey = Config.Tool.AiChatApiKey;
        return !string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(model) &&
               (isLocalProvider || !string.IsNullOrWhiteSpace(apiKey));
    }

    /// <summary>
    ///     调用 AI 对最近一次崩溃做深入分析，返回 AI 生成的诊断文本。
    /// </summary>
    public static string Analyze(RecentCrashInfo crash, Action<string>? onDelta = null,
        CancellationToken token = default)
    {
        var baseUrl = Config.Tool.AiChatBaseUrl;
        var apiKey = Config.Tool.AiChatApiKey;
        var model = Config.Tool.AiChatModel;
        var isLocal = Config.Tool.AiChatProvider > 0;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model) ||
            (!isLocal && string.IsNullOrWhiteSpace(apiKey)))
            throw new AiChatException(Lang.Text("CrashAgent.Error.NotConfigured"));

        var userPrompt =
            $"检测到一次游戏崩溃，时间：{crash.DetectedAt}，游戏版本：{(string.IsNullOrEmpty(crash.Version) ? "未知" : crash.Version)}。\n" +
            $"本地规则分析结果如下：\n{crash.Reason}\n" +
            $"关键证据：\n{crash.Suggestion}\n\n请给出深入诊断。";

        var messages = new List<AiChatMessage>
        {
            new() { Role = "user", Content = userPrompt }
        };

        var sb = new System.Text.StringBuilder();
        ModAiChat.SendChatAsync(
                messages,
                baseUrl,
                apiKey,
                model,
                SystemPrompt,
                delta =>
                {
                    sb.Append(delta);
                    onDelta?.Invoke(delta);
                },
                token)
            .GetAwaiter()
            .GetResult();

        var text = sb.ToString().Trim();
        if (text.Length == 0)
            throw new AiChatException(Lang.Text("CrashAgent.Error.EmptyResponse"));
        return text;
    }
}
