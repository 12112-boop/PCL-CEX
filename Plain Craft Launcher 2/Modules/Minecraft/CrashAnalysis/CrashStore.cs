using System.Text.Json;
using System.IO;
using PCL.Core.Utils;

namespace PCL;

/// <summary>
///     最近一次崩溃分析的展示数据，供「AI 崩溃分析」卡片显示。
/// </summary>
public sealed record RecentCrashInfo
{
    public required string DetectedAt { get; init; }
    public required string Version { get; init; }
    public required string Reason { get; init; }
    public required string Suggestion { get; init; }
    public required string RawSummary { get; init; }
}

/// <summary>
///     崩溃分析结果的跨进程持久化（JSON 文件存于 PCL 根目录），
///     游戏进程崩溃后启动器主进程读取并展示在首页。
/// </summary>
public static class CrashStore
{
    private static readonly object _lock = new();

    private static string Path_ => Path.Combine(ModBase.exePath, "CrashReport.json");

    public static RecentCrashInfo? Load()
    {
        try
        {
            if (!File.Exists(Path_))
                return null;
            var json = File.ReadAllText(Path_);
            return JsonSerializer.Deserialize<RecentCrashInfo>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(RecentCrashInfo info)
    {
        lock (_lock)
        {
            try
            {
                File.WriteAllText(Path_, JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = false }));
            }
            catch
            {
                // 忽略写入失败
            }
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(Path_))
                    File.Delete(Path_);
            }
            catch
            {
            }
        }
    }
}
