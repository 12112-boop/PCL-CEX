using System;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.OS;

namespace PCL.Core.App;

// ReSharper disable InconsistentNaming
public static class Secrets
{
    /// <summary>
    /// 微软 OAuth 的 Client ID。
    /// 优先使用环境变量 MS_CLIENT_ID，其次使用 Azure Portal 注册的应用 ID。
    /// </summary>
    public static string MSOAuthClientId
    {
        get
        {
            // 1. 环境变量优先（支持用户自定义）
            var envValue = Environment.GetEnvironmentVariable("MS_CLIENT_ID");
            if (!string.IsNullOrWhiteSpace(envValue))
                return envValue.Trim();

            // 2. 内部调试密钥（Debug 模式可通过 GetSecret 注入）
            var debugValue = EnvironmentInterop.GetSecret("MS_CLIENT_ID", readEnvDebugOnly: false).ReplaceNullOrEmpty();
            if (!string.IsNullOrWhiteSpace(debugValue))
                return debugValue;

            // 3. Prism Launcher 的公开 Client ID（第三方多租户应用，支持个人账户 + XboxLive.signin）
            return "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";
        }
    }

    /// <summary>
    /// CurseForge API 的 Client ID
    /// </summary>
    public static string CurseForgeAPIKey { get; } = EnvironmentInterop.GetSecret("CURSEFORGE_API_KEY", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// 遥测密钥
    /// </summary>
    public static string TelemetryKey { get; } = EnvironmentInterop.GetSecret("TELEMETRY_KEY", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// Natayark ID OAuth 的 Client ID
    /// </summary>
    public static string NatayarkClientId { get; } = EnvironmentInterop.GetSecret("NAID_CLIENT_ID", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// Natayark ID OAuth 的 Client ID
    /// </summary>
    public static string NatayarkClientSecret { get; } = EnvironmentInterop.GetSecret("NAID_CLIENT_SECRET", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// 联机根服务器
    /// </summary>
    public static string[] LinkServers { get; } = EnvironmentInterop.GetSecret("LINK_SERVER_ROOT", readEnvDebugOnly: true).ReplaceNullOrEmpty().Split("|");

    /// <summary>
    /// 当前版本的 Git 提交 SHA
    /// </summary>
    public static string CommitHash { get; } = EnvironmentInterop.GetSecret("GITHUB_SHA", readEnvDebugOnly: true).ReplaceNullOrEmpty();
}
