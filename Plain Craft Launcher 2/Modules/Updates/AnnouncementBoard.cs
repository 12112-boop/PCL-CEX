using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PCL.Core.App;
using PCL.Core.IO.Net.Http;
using PCL.Network;

namespace PCL;

/// <summary>
///     PCL-CEX 公告板：从 GitHub 仓库读取 announcement/announcements.json，
///     并缓存到本地，供启动页右上角的公告栏展示。
/// </summary>
public static class AnnouncementBoard
{
    /// <summary>公告文件在仓库中的相对路径。</summary>
    private const string FilePath = "announcement/announcements.json";

    private const string ApiTemplate = "https://api.github.com/repos/{0}/contents/" + FilePath;
    private const string CdnTemplate = "https://cdn.jsdelivr.net/gh/{0}@main/" + FilePath;

    private static readonly object Lock = new();
    private static List<AnnouncementItem> _items = new();
    private static bool _loaded;

    /// <summary>公告刷新完成（无论成功与否）。</summary>
    public static event Action Updated;

    /// <summary>当前公告列表（已按置顶 + 时间排序）。</summary>
    public static IReadOnlyList<AnnouncementItem> Items
    {
        get
        {
            lock (Lock)
            {
                return _items;
            }
        }
    }

    /// <summary>最近一次刷新是否成功。</summary>
    public static bool LastLoadSucceeded { get; private set; }

    /// <summary>最近一次刷新时间。</summary>
    public static DateTime LastLoadTime { get; private set; } = DateTime.MinValue;

    /// <summary>本地缓存文件路径。</summary>
    private static string CacheFile => Path.Combine(ModBase.pathTemp, "CEAnnouncementBoard.json");

    /// <summary>
    ///     是否已经尝试加载过。
    /// </summary>
    public static bool HasLoaded
    {
        get
        {
            lock (Lock)
            {
                return _loaded;
            }
        }
    }

    /// <summary>
    ///     启动时调用：先读本地缓存立即展示，再去网络刷新。
    /// </summary>
    public static void LoadOnStartup()
    {
        ModBase.RunInNewThread(() =>
        {
            LoadFromCache();
            Refresh();
        }, "加载公告板");
    }

    /// <summary>
    ///     从网络刷新公告，失败时保留缓存内容。
    /// </summary>
    public static void Refresh()
    {
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var json = FetchJson();
                var list = Parse(json);
                lock (Lock)
                {
                    _items = list;
                    _loaded = true;
                }

                LastLoadSucceeded = true;
                LastLoadTime = DateTime.Now;
                SaveCache(json);
                ModBase.Log($"[Announcement] 已加载 {list.Count} 条公告");
            }
            catch (Exception ex)
            {
                // 仓库里还没有公告文件时按"没有公告"处理，不当作错误
                if (ex is HttpResponseException { Response.StatusCode: HttpStatusCode.NotFound })
                {
                    lock (Lock)
                    {
                        _items = new List<AnnouncementItem>();
                        _loaded = true;
                    }

                    LastLoadSucceeded = true;
                    LastLoadTime = DateTime.Now;
                    ModBase.Log("[Announcement] 仓库暂无公告文件，视为无公告");
                }
                else
                {
                    LastLoadSucceeded = false;
                    LastLoadTime = DateTime.Now;
                    ModBase.Log(ex, "[Announcement] 获取公告失败，沿用本地缓存", ModBase.LogLevel.Debug);
                }
            }
            finally
            {
                Updated?.Invoke();
            }
        }, "刷新公告板", ThreadPriority.BelowNormal);
    }

    private static string FetchJson()
    {
        var repository = ResolveRepository();
        Exception? lastError = null;

        // 优先走 GitHub API 的 raw 内容：无 CDN 缓存，推送后立即可见
        try
        {
            return Requester.FetchString(string.Format(ApiTemplate, repository), new RequestParam
            {
                Accept = "application/vnd.github.raw",
                UseBrowserUserAgent = true,
                Timeout = 15000,
                Retries = 2
            });
        }
        catch (Exception ex)
        {
            lastError = ex;
            ModBase.Log(ex, "[Announcement] GitHub API 读取失败，改用 jsDelivr", ModBase.LogLevel.Debug);
        }

        // 备用：jsDelivr（国内可达性好），但对 @main 有较长缓存
        try
        {
            return Requester.FetchString(string.Format(CdnTemplate, repository), new RequestParam
            {
                Timeout = 15000,
                Retries = 1
            });
        }
        catch (Exception ex)
        {
            throw lastError ?? ex;
        }
    }

    private static string ResolveRepository()
    {
        try
        {
            var configured = Config.Update.Repository;
            if (!string.IsNullOrWhiteSpace(configured) && configured.Contains('/'))
                return configured.Trim().Trim('/');
        }
        catch
        {
            // 配置尚未就绪时忽略
        }

        return UpdatesGitHubReleaseModel.DefaultRepository;
    }

    private static List<AnnouncementItem> Parse(string json)
    {
        var node = ModBase.GetJson(json);
        var array = node?["items"]?.AsArray();
        if (array is null)
            return new List<AnnouncementItem>();

        var now = DateTime.Now;
        var result = new List<AnnouncementItem>();
        foreach (var entry in array)
        {
            if (entry is null)
                continue;
            try
            {
                var item = JsonSerializer.Deserialize<AnnouncementItem>(entry.ToJsonString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (item is null || string.IsNullOrWhiteSpace(item.Title))
                    continue;
                if (item.IsExpired(now))
                    continue;
                result.Add(item);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Announcement] 跳过一条无法解析的公告", ModBase.LogLevel.Debug);
            }
        }

        return result
            .OrderByDescending(i => i.Pinned)
            .ThenByDescending(i => i.ParsedTime ?? DateTime.MinValue)
            .ToList();
    }

    private static void SaveCache(string json)
    {
        try
        {
            File.WriteAllText(CacheFile, json);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Announcement] 写入公告缓存失败", ModBase.LogLevel.Debug);
        }
    }

    private static void LoadFromCache()
    {
        try
        {
            if (!File.Exists(CacheFile))
                return;
            var list = Parse(File.ReadAllText(CacheFile));
            lock (Lock)
            {
                _items = list;
                _loaded = true;
            }

            Updated?.Invoke();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Announcement] 读取公告缓存失败", ModBase.LogLevel.Debug);
        }
    }

    /// <summary>
    ///     把一条公告标记为已读。
    /// </summary>
    public static void MarkRead(AnnouncementItem item)
    {
        if (item is null)
            return;
        try
        {
            var read = ReadIds;
            if (read.Contains(item.Id))
                return;
            read.Add(item.Id);
            States.Hint.ReadAnnouncements = string.Join("|", read.TakeLast(200));
            item.IsRead = true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Announcement] 记录已读失败", ModBase.LogLevel.Debug);
        }
    }

    /// <summary>已读公告 ID 列表。</summary>
    public static List<string> ReadIds
    {
        get
        {
            try
            {
                return States.Hint.ReadAnnouncements
                    .Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }
    }
}

/// <summary>单条公告。</summary>
public class AnnouncementItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("title")] public string Title { get; set; } = "";

    [JsonPropertyName("content")] public string Content { get; set; } = "";

    /// <summary>info / important / warning</summary>
    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    [JsonPropertyName("pinned")] public bool Pinned { get; set; }

    [JsonPropertyName("time")] public string Time { get; set; } = "";

    [JsonPropertyName("expire")] public string Expire { get; set; } = "";

    [JsonIgnore] public bool IsRead { get; set; }

    /// <summary>解析后的发布时间。</summary>
    [JsonIgnore]
    public DateTime? ParsedTime => ParseDate(Time);

    /// <summary>是否已过期。</summary>
    public bool IsExpired(DateTime now)
    {
        var expireAt = ParseDate(Expire);
        return expireAt.HasValue && expireAt.Value < now;
    }

    private static DateTime? ParseDate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (DateTime.TryParse(text, out var dt))
            return dt;
        return null;
    }

    /// <summary>是否为新公告（未读）。</summary>
    public bool IsNew => !string.IsNullOrEmpty(Id) && !AnnouncementBoard.ReadIds.Contains(Id);

    public AnnouncementLevel LevelEnum => Level?.ToLowerInvariant() switch
    {
        "warning" => AnnouncementLevel.Warning,
        "important" => AnnouncementLevel.Important,
        _ => AnnouncementLevel.Info
    };
}

public enum AnnouncementLevel
{
    Info = 0,
    Important = 1,
    Warning = 2
}
