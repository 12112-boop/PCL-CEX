using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Http;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>
///     PCL-CEX 自有更新源：直接读取 GitHub Releases 的公开 API。
///     Release 的 tag 作为版本号，exe 资产作为更新文件，body 作为更新日志。
///     不需要服务器、不需要 CDK，仓库改名只需改 <see cref="DefaultRepository" /> 或配置项。
/// </summary>
public class UpdatesGitHubReleaseModel : IUpdateSource
{
    /// <summary>默认仓库（owner/repo）。可在「设置 → 更新」中覆盖。</summary>
    public const string DefaultRepository = "12112-boop/PCL-CEX";

    private const string ApiBase = "https://api.github.com/repos/";

    private VersionDataModel _cached;
    private DateTime _cachedAt = DateTime.MinValue;
    /// <summary>上次请求时仓库还没有任何 Release（404），同样缓存 5 分钟，避免反复报错。</summary>
    private bool _cachedNoRelease;

    public UpdatesGitHubReleaseModel(string repository = null)
    {
        Repository = string.IsNullOrWhiteSpace(repository) ? DefaultRepository : repository.Trim();
    }

    /// <summary>当前使用的仓库（owner/repo）。</summary>
    public string Repository { get; set; }

    public string SourceName { get; set; } = "GitHub Releases";

    public bool IsAvailable()
    {
        return !string.IsNullOrWhiteSpace(Repository) && Repository.Contains('/');
    }

    /// <summary>强制丢弃缓存，下次读取重新请求。</summary>
    public bool RefreshCache()
    {
        _cached = null;
        _cachedAt = DateTime.MinValue;
        _cachedNoRelease = false;
        return true;
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        // 只有一个通道，channel / arch 参数保留是为了满足接口，实际不使用。
        return FetchLatest();
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        // 仓库还没发过 Release 时不算"有更新"，静默视为最新，避免每次启动都弹错误提示
        var latest = FetchLatest(throwIfMissing: false);
        if (latest is null)
        {
            ModBase.Log($"[Update] 仓库 {Repository} 暂无 Release，跳过版本比较");
            return true;
        }

        if (!SemVer.TryParse(latest.VersionName, out var remote) || remote is null)
            throw new Exception($"远程版本号无法解析：{latest.VersionName}");
        return currentVersion >= remote;
    }

    public VersionAnnouncementDataModel GetAnnouncementList()
    {
        // 本更新源不提供公告；返回空列表让 AnnouncementService 静默跳过。
        return new VersionAnnouncementDataModel { Content = new List<VersionAnnouncementContentModel>() };
    }

    public List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        var latest = FetchLatest();
        var loaders = new List<ModLoader.LoaderBase>();
        loaders.Add(new ModLoader.LoaderTask<int, List<DownloadFile>>(Lang.Text("Update.Task.GetDownloadInfo"), load =>
        {
            if (string.IsNullOrWhiteSpace(latest.DownloadUrl))
                throw new Exception("该版本没有可下载的 exe 资产");
            // 附带 SHA256 校验，下载器会在完成后自动比对
            var checker = string.IsNullOrWhiteSpace(latest.Sha256)
                ? null
                : new ModBase.FileChecker(hash: latest.Sha256, canUseExistsFile: false);
            load.output = new List<DownloadFile>
            {
                new(new[] { latest.DownloadUrl }, output, checker)
            };
        }));
        loaders.Add(new LoaderDownload(Lang.Text("Update.Task.DownloadUpdateFile"), new List<DownloadFile>()));
        return loaders;
    }

    private VersionDataModel FetchLatest(bool throwIfMissing = true)
    {
        var cached = (DateTime.Now - _cachedAt).TotalMinutes < 5;
        if (cached && _cached is not null)
            return _cached;
        if (cached && _cachedNoRelease)
        {
            if (throwIfMissing)
                throw new Exception($"仓库 {Repository} 还没有发布任何 Release，无法获取更新");
            return null;
        }

        JsonNode json;
        try
        {
            json = Requester.FetchJson($"{ApiBase}{Repository}/releases/latest", new RequestParam
            {
                Accept = "application/vnd.github+json",
                Timeout = 30000,
                Retries = 2,
                UseBrowserUserAgent = true
            });
        }
        catch (HttpResponseException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            // 仓库存在但一个 Release 都没发（GitHub 对 /releases/latest 返回 404）
            _cached = null;
            _cachedNoRelease = true;
            _cachedAt = DateTime.Now;
            ModBase.Log($"[Update] 仓库 {Repository} 暂无 Release");
            if (throwIfMissing)
                throw new Exception($"仓库 {Repository} 还没有发布任何 Release，无法获取更新");
            return null;
        }

        _cachedNoRelease = false;
        var tag = json["tag_name"]?.ToString();
        if (string.IsNullOrWhiteSpace(tag))
            throw new Exception("GitHub Release 缺少 tag_name");

        var versionName = NormalizeTag(tag);
        var body = json["body"]?.ToString() ?? "";

        var exeAsset = json["assets"]?.AsArray()
            .FirstOrDefault(asset =>
            {
                var name = asset?["name"]?.ToString() ?? "";
                return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            });

        var downloadUrl = exeAsset?["browser_download_url"]?.ToString();
        var sha256 = exeAsset?["digest"]?.ToString();
        if (!string.IsNullOrWhiteSpace(sha256) && sha256.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            sha256 = sha256["sha256:".Length..];

        _cached = new VersionDataModel
        {
            Source = SourceName,
            VersionName = versionName,
            VersionCode = 0,
            Changelog = body,
            Sha256 = sha256,
            DownloadUrl = downloadUrl
        };
        _cachedAt = DateTime.Now;
        ModBase.Log($"[Update] GitHub Release 最新版本：{versionName}（{Repository}）");
        return _cached;
    }

    /// <summary>把 tag 规整成 SemVer：去掉前缀 v、把 2.1.5.1 之类截断到三段。</summary>
    private static string NormalizeTag(string tag)
    {
        var text = tag.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            text = text[1..];
        if (SemVer.TryParse(text, out _))
            return text;
        // 允许 2.1.5.1 → 2.1.5
        var parts = text.Split('.');
        if (parts.Length > 3)
        {
            var trimmed = string.Join(".", parts.Take(3));
            if (SemVer.TryParse(trimmed, out _))
                return trimmed;
        }

        return text;
    }
}
