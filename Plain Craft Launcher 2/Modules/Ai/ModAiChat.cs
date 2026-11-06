using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Ai;

/// <summary>
/// 单条聊天消息。
/// </summary>
public class AiChatMessage
{
    /// <summary>角色：system / user / assistant。</summary>
    public string Role { get; set; } = "user";

    /// <summary>消息内容。</summary>
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// AI 聊天服务异常，携带可读的错误信息。
/// </summary>
public class AiChatException : Exception
{
    public AiChatException(string message) : base(message)
    {
    }

    public AiChatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// 基于 OpenAI 兼容接口（/chat/completions）的 AI 聊天服务，支持流式输出。
/// 兼容 OpenAI、DeepSeek、Moonshot(Kimi)、智谱等提供相同接口的服务。
/// </summary>
public static class ModAiChat
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    /// <summary>
    /// 发送聊天请求并流式接收回复。
    /// </summary>
    /// <param name="messages">历史消息（不含 system 提示词）。</param>
    /// <param name="baseUrl">接口基础地址，例如 https://api.deepseek.com/v1。</param>
    /// <param name="apiKey">API Key。</param>
    /// <param name="model">模型名称。</param>
    /// <param name="systemPrompt">可选的系统提示词。</param>
    /// <param name="onDelta">每收到一段增量文本时触发。</param>
    /// <param name="token">取消令牌。</param>
    public static async Task SendChatAsync(
        IReadOnlyList<AiChatMessage> messages,
        string baseUrl,
        string apiKey,
        string model,
        string systemPrompt,
        Action<string> onDelta,
        CancellationToken token = default)
    {
        if (messages.Count == 0) throw new AiChatException("没有可发送的消息。");
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new AiChatException("未配置接口地址。");
        if (string.IsNullOrWhiteSpace(model)) throw new AiChatException("未配置模型名称。");

        var requestMessages = new List<AiChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            requestMessages.Add(new AiChatMessage { Role = "system", Content = systemPrompt });
        requestMessages.AddRange(messages);

        var payload = new
        {
            model,
            messages = requestMessages.ConvertAll(m => new { role = m.Role, content = m.Content }),
            stream = true,
            temperature = 0.7
        };
        var json = JsonSerializer.Serialize(payload);
        var url = baseUrl.TrimEnd('/') + "/chat/completions";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Add("Authorization", "Bearer " + apiKey.Trim());
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AiChatException($"无法连接到 {url}：{ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = string.Empty;
                try
                {
                    errorBody = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                }
                catch
                {
                    // 忽略错误响应体读取失败
                }
                throw new AiChatException($"接口返回错误（HTTP {(int)response.StatusCode}）：{ExtractError(errorBody)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0 || !line.StartsWith("data:")) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;

                string delta = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                        choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                    {
                        var first = choices[0];
                        if (first.TryGetProperty("delta", out var d) &&
                            d.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                            delta = c.GetString();
                    }
                }
                catch (JsonException)
                {
                    // 跳过无法解析的行（如部分提供商的注释行）
                }
                if (!string.IsNullOrEmpty(delta)) onDelta(delta);
            }
        }
    }

    /// <summary>
    /// 测试接口连通性：发送一个最小请求，成功时返回模型回复内容。
    /// </summary>
    public static async Task<string> TestConnectionAsync(
        string baseUrl, string apiKey, string model, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new AiChatException("未配置接口地址。");
        if (string.IsNullOrWhiteSpace(model)) throw new AiChatException("未配置模型名称。");

        var payload = new
        {
            model,
            messages = new[] { new { role = "user", content = "ping" } },
            max_tokens = 1,
            stream = false
        };
        var json = JsonSerializer.Serialize(payload);
        var url = baseUrl.TrimEnd('/') + "/chat/completions";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Add("Authorization", "Bearer " + apiKey.Trim());
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AiChatException($"无法连接到 {url}：{ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new AiChatException($"接口返回错误（HTTP {(int)response.StatusCode}）：{ExtractError(body)}");
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("content", out var content))
                        return content.GetString() ?? string.Empty;
                }
                return string.Empty;
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// 获取服务端可用的模型列表（OpenAI 兼容的 /models 接口）。
    /// Ollama 与 LM Studio 等本地服务同样支持该接口。
    /// </summary>
    public static async Task<List<string>> ListModelsAsync(
        string baseUrl, string apiKey, CancellationToken token = default)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new AiChatException("未配置接口地址。");

        var url = baseUrl.TrimEnd('/') + "/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Add("Authorization", "Bearer " + apiKey.Trim());

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AiChatException($"无法连接到 {url}：{ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new AiChatException($"接口返回错误（HTTP {(int)response.StatusCode}）：{ExtractError(body)}");
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idEl) &&
                            idEl.ValueKind == JsonValueKind.String)
                        {
                            var id = idEl.GetString();
                            if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // 非 JSON 响应，返回空列表
            }
        }
        return result;
    }

    /// <summary>
    /// 从错误响应体中提取可读信息。
    /// </summary>
    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "无详细信息。";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                    return message.GetString() ?? body;
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? body;
            }
            if (doc.RootElement.TryGetProperty("message", out var msg) &&
                msg.ValueKind == JsonValueKind.String)
                return msg.GetString() ?? body;
        }
        catch (JsonException)
        {
            // 非 JSON 错误响应，直接返回原文
        }
        // 过长的响应体截断显示
        return body.Length > 300 ? body[..300] + "..." : body;
    }
}
