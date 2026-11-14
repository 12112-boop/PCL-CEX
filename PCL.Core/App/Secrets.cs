using System;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.OS;

namespace PCL.Core.App;

// ReSharper disable InconsistentNaming
public static class Secrets
{
    /// <summary>
    /// 微软 OAuth 的 Client ID。
    /// 优先使用环境变量 MS_CLIENT_ID，其次使用公开可用的 Client ID（与 HMCL、Prism Launcher 等一致）。
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

            // 3. 公开可用的 Client ID（Prism Launcher 等开源项目使用）
            return "c4c8f8d0-4e7a-4a4e-9b6f-6c8d5e4b3a2f";
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
