using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft;

/// <summary>
/// 已安装 Mod 的基础信息。
/// </summary>
public class ModFileEntry
{
    /// <summary>Mod 文件名。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Mod ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Mod 版本。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Mod 显示名称。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>元数据来源加载器类型：fabric / quilt / forge / neoforge / legacyforge。</summary>
    public string Loader { get; set; } = "fabric";

    /// <summary>该 Mod 声明的其他等价 ID（Fabric 的 provides）。</summary>
    public List<string> Provides { get; set; } = [];

    /// <summary>依赖声明：依赖 ID -> 版本范围。</summary>
    public Dictionary<string, string> Depends { get; set; } = [];

    /// <summary>是否通过 Jar-in-Jar 内嵌了其他 Mod。</summary>
    public bool HasJarInJar { get; set; }
}

/// <summary>
/// 缺失的前置依赖。
/// </summary>
public class ModMissingDependency
{
    /// <summary>依赖 ID（通常与 Modrinth 项目 ID 一致）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Mod 声明的版本范围。</summary>
    public string VersionRange { get; set; } = string.Empty;

    /// <summary>需要该依赖的 Mod ID。</summary>
    public string RequiredBy { get; set; } = string.Empty;

    /// <summary>目标加载器。</summary>
    public string Loader { get; set; } = "fabric";

    /// <summary>目标游戏版本。</summary>
    public string GameVersion { get; set; } = string.Empty;

    /// <summary>匹配到的文件名称。</summary>
    public string? FileName { get; set; }

    /// <summary>匹配到的下载地址。</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>匹配到的版本号。</summary>
    public string? VersionNumber { get; set; }

    /// <summary>匹配到的项目标题。</summary>
    public string? ProjectTitle { get; set; }

    /// <summary>匹配到的 Modrinth 项目 ID。</summary>
    public string? ProjectId { get; set; }

    /// <summary>解析来源：modrinth 或 curseforge。</summary>
    public string Source { get; set; } = "modrinth";
}

/// <summary>
/// 一次扫描的汇总结果。
/// </summary>
public class ModScanResult
{
    /// <summary>识别出的全部 Mod。</summary>
    public List<ModFileEntry> Mods { get; } = [];

    /// <summary>缺失的硬性前置依赖。</summary>
    public List<ModMissingDependency> Missing { get; } = [];

    /// <summary>重复安装的 Mod（同一 Mod ID 存在多个文件）。</summary>
    public List<string> Duplicates { get; } = [];

    /// <summary>无法识别元数据的 jar 文件。</summary>
    public List<string> Unrecognized { get; } = [];

    /// <summary>内嵌（Jar-in-Jar）子 jar 的总数。</summary>
    public int JarInJarCount { get; set; }

    /// <summary>实例使用的加载器。</summary>
    public string Loader { get; set; } = "fabric";

    /// <summary>实例的游戏版本。</summary>
    public string GameVersion { get; set; } = string.Empty;

    /// <summary>被扫描的 mods 目录。</summary>
    public string ModsPath { get; set; } = string.Empty;

    /// <summary>扫描完成时间。</summary>
    public DateTime ScannedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Modrinth 搜索候选项。
/// </summary>
public class ModFixCandidate
{
    public string ProjectId { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Mod 前置修补：扫描实例 mods 目录中缺失的前置库，并从 Modrinth 下载安装。
/// </summary>
public static class ModModPatch
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private const string ModrinthApi = "https://api.modrinth.com/v2";
    private const string CurseForgeApi = "https://api.curseforge.com";
    private const string UserAgent = "PCL-CE/ModPatch (contact: PCL-Community)";

    /// <summary>
    /// Minecraft 在 CurseForge 上的游戏 ID。
    /// </summary>
    private const int CurseForgeGameId = 432;

    /// <summary>
    /// Mods 分类在 CurseForge 上的 class ID。
    /// </summary>
    private const int CurseForgeModClassId = 6;

    /// <summary>
    /// CurseForge API Key（由用户在工具页面配置，加密存储于配置中）。
    /// 未配置时不会尝试从 CurseForge 解析。
    /// </summary>
    public static string? CurseForgeApiKey { get; set; }

    /// <summary>
    /// 无需修补的依赖（加载器自身与运行环境）。
    /// </summary>
    private static readonly HashSet<string> IgnoredDependencies =
    [
        "minecraft", "java", "jvm", "fabricloader", "fabric", "quilt_loader", "quilt-loader", "quilt-base",
        "quilted_fabric_api_hidden", "forge", "neoforge", "fml", "modloader", "liteloader", "bootstrap"
    ];

    /// <summary>
    /// 列出所有包含 mods 使用可能的实例（.minecraft/versions 下的目录名）。
    /// </summary>
    public static List<string> ListInstances()
    {
        var result = new List<string>();
        var versionsDir = Path.Combine(ModFolder.mcFolderSelected, "versions");
        if (!Directory.Exists(versionsDir)) return result;
        result.AddRange(from dir in Directory.GetDirectories(versionsDir)
                        let name = Path.GetFileName(dir)
                        where !name.StartsWith('.')
                        orderby name
                        select name);
        return result;
    }

    /// <summary>
    /// 获取指定实例的 mods 目录。
    /// </summary>
    public static string GetModsPath(string instanceName) =>
        Path.Combine(ModFolder.mcFolderSelected, "versions", instanceName, "mods");

    /// <summary>
    /// 检测实例使用的加载器与游戏版本。
    /// </summary>
    public static (string Loader, string GameVersion) DetectLoaderAndGameVersion(string instanceName)
    {
        var jsonPath = Path.Combine(ModFolder.mcFolderSelected, "versions", instanceName, instanceName + ".json");
        if (!File.Exists(jsonPath)) return (GuessLoaderFromName(instanceName), ExtractGameVersion(instanceName));

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;
            var gameVersion = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()!
                : instanceName;
            if (root.TryGetProperty("inheritsFrom", out var inherit) && inherit.ValueKind == JsonValueKind.String)
                gameVersion = inherit.GetString()!;
            var libraries = root.TryGetProperty("libraries", out var libEl) ? libEl.ToString() : string.Empty;
            // NeoForge 1.20.2+ 使用 net.neoforged.fancymodloader:loader 等坐标，旧版为 net.neoforged:neoforge
            if (libraries.Contains("net.neoforged", StringComparison.OrdinalIgnoreCase))
                return ("neoforge", ExtractGameVersion(gameVersion));
            if (libraries.Contains("net.minecraftforge", StringComparison.OrdinalIgnoreCase))
                return ("forge", ExtractGameVersion(gameVersion));
            if (libraries.Contains("org.quiltmc:quilt-loader", StringComparison.OrdinalIgnoreCase))
                return ("quilt", ExtractGameVersion(gameVersion));
            if (libraries.Contains("net.fabricmc:fabric-loader", StringComparison.OrdinalIgnoreCase))
                return ("fabric", ExtractGameVersion(gameVersion));
            // 库信息无法判断时，回退到实例名称猜测
            return (GuessLoaderFromName(instanceName), ExtractGameVersion(gameVersion));
        }
        catch
        {
            return (GuessLoaderFromName(instanceName), ExtractGameVersion(instanceName));
        }
    }

    /// <summary>
    /// 根据实例名称猜测加载器，例如 1.21.1-NeoForge_21.1.264。
    /// </summary>
    private static string GuessLoaderFromName(string instanceName)
    {
        var name = (instanceName ?? "").ToLowerInvariant();
        if (name.Contains("neoforge")) return "neoforge";
        if (name.Contains("quilt")) return "quilt";
        if (name.Contains("fabric")) return "fabric";
        if (name.Contains("forge")) return "forge";
        return "fabric";
    }

    private static string ExtractGameVersion(string versionName)
    {
        // 从 1.20.1-fabric 之类的名称中提取游戏版本
        var index = versionName.IndexOf("1.", StringComparison.Ordinal);
        if (index < 0) return versionName;
        var text = versionName[index..];
        var match = System.Text.RegularExpressions.Regex.Match(text, @"^\d+\.\d+(\.\d+)?");
        return match.Success ? match.Value : text;
    }

    /// <summary>
    /// 扫描 mods 目录，读取每个 Mod 的元数据。
    /// </summary>
    public static ModScanResult Scan(string modsPath, string loader = "fabric", string gameVersion = "")
    {
        var result = new ModScanResult
        {
            ModsPath = modsPath,
            Loader = loader,
            GameVersion = gameVersion,
            ScannedAt = DateTime.Now
        };
        if (!Directory.Exists(modsPath)) return result;

        foreach (var file in Directory.GetFiles(modsPath, "*.jar").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var entry = ReadModEntry(file);
                if (entry is null)
                {
                    result.Unrecognized.Add(Path.GetFileName(file));
                    continue;
                }
                result.Mods.Add(entry);
                if (entry.HasJarInJar) result.JarInJarCount += 1;
            }
            catch
            {
                // 跳过无法读取的 jar
                result.Unrecognized.Add(Path.GetFileName(file));
            }
        }

        // 重复 Mod 检测
        result.Duplicates.AddRange(
            from mod in result.Mods
            where !string.IsNullOrWhiteSpace(mod.Id)
            group mod by mod.Id.ToLowerInvariant() into g
            where g.Count() > 1
            select $"{g.Key} ← {string.Join(", ", g.Select(m => m.FileName))}");

        // 若调用方未提供加载器信息，则根据扫描结果推断
        if (string.IsNullOrEmpty(result.Loader) || result.Loader == "fabric")
        {
            var inferred = InferLoader(result.Mods);
            if (inferred is not null && inferred != "legacyforge" && inferred != "quilt") result.Loader = inferred;
        }

        // 计算缺失的前置依赖
        var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in result.Mods)
        {
            if (!string.IsNullOrWhiteSpace(mod.Id)) provided.Add(mod.Id);
            foreach (var p in mod.Provides) provided.Add(p);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in result.Mods)
        {
            foreach (var (depId, range) in mod.Depends)
            {
                if (IgnoredDependencies.Contains(depId)) continue;
                if (provided.Contains(depId)) continue;
                if (!seen.Add(depId)) continue;
                result.Missing.Add(new ModMissingDependency
                {
                    Id = depId,
                    VersionRange = range,
                    RequiredBy = mod.Id,
                    // 使用声明该依赖的 Mod 自身的加载器，旧版 Forge 统一归入 forge
                    Loader = mod.Loader == "legacyforge" ? "forge" : mod.Loader,
                    GameVersion = result.GameVersion
                });
            }
        }

        return result;
    }

    private static string? InferLoader(List<ModFileEntry> mods)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            if (string.IsNullOrWhiteSpace(mod.Loader)) continue;
            counts[mod.Loader] = counts.TryGetValue(mod.Loader, out var c) ? c + 1 : 1;
        }
        var best = counts.OrderByDescending(p => p.Value).FirstOrDefault();
        return best.Key;
    }

    /// <summary>
    /// 读取单个 jar 的 Mod 元数据，支持 Fabric / Quilt / Forge / NeoForge / 旧版 Forge。
    /// </summary>
    public static ModFileEntry? ReadModEntry(string file)
    {
        using var zip = ZipFile.OpenRead(file);
        return ReadModEntry(zip, Path.GetFileName(file));
    }

    private static ModFileEntry? ReadModEntry(ZipArchive zip, string fileName, int depth = 0)
    {
        ModFileEntry? entry = null;

        // Fabric / Quilt
        var fabricEntry = zip.GetEntry("fabric.mod.json") ?? zip.GetEntry("quilt.mod.json");
        if (fabricEntry is not null)
        {
            using var stream = fabricEntry.Open();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl))
            {
                var id = idEl.GetString() ?? string.Empty;
                var version = root.TryGetProperty("version", out var verEl) ? verEl.ToString() : string.Empty;
                var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;

                var provides = new List<string>();
                if (root.TryGetProperty("provides", out var provEl) && provEl.ValueKind == JsonValueKind.Array)
                    provides.AddRange(provEl.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!));

                var depends = new Dictionary<string, string>();
                if (root.TryGetProperty("depends", out var depEl) && depEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in depEl.EnumerateObject())
                        depends[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString() ?? "*"
                            : "*";
                }

                entry = new ModFileEntry
                {
                    FileName = fileName,
                    Id = id,
                    Version = version,
                    DisplayName = name,
                    Loader = zip.GetEntry("quilt.mod.json") is not null ? "quilt" : "fabric",
                    Provides = provides,
                    Depends = depends
                };
            }
        }

        // Forge / NeoForge（1.13+），注意 NeoForge 的元数据文件名为 neoforge.mods.toml
        if (entry is null)
        {
            var tomlEntry = zip.GetEntry("META-INF/mods.toml") ?? zip.GetEntry("META-INF/neoforge.mods.toml");
            if (tomlEntry is not null)
            {
                var isNeoForge = zip.GetEntry("META-INF/mods.toml") is null;
                using var stream = tomlEntry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                entry = ParseModsToml(reader.ReadToEnd(), fileName, isNeoForge ? "neoforge" : "forge");
            }
        }

        // 旧版 Forge（mcmod.info）
        if (entry is null)
        {
            var infoEntry = zip.GetEntry("mcmod.info");
            if (infoEntry is not null)
            {
                using var stream = infoEntry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                entry = ParseMcModInfo(reader.ReadToEnd(), fileName);
            }
        }

        if (entry is null) return null;

        // Jar-in-Jar：读取内嵌子 jar 提供的 Mod ID 与其依赖声明
        // Fabric 使用 META-INF/jars/，NeoForge 的 JarJar 使用 META-INF/jarjar/
        foreach (var jij in zip.Entries.Where(e =>
                     (e.FullName.StartsWith("META-INF/jars/", StringComparison.OrdinalIgnoreCase) ||
                      e.FullName.StartsWith("META-INF/jarjar/", StringComparison.OrdinalIgnoreCase)) &&
                     e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var jijStream = jij.Open();
                using var jijZip = new ZipArchive(jijStream, ZipArchiveMode.Read);
                // 内嵌 jar 自身也可能再次内嵌，限制递归深度避免异常结构导致死循环
                if (depth < 2)
                {
                    var nested = ReadModEntry(jijZip, Path.GetFileName(jij.FullName), depth + 1);
                    if (nested is not null)
                    {
                        if (!string.IsNullOrEmpty(nested.Id) &&
                            !entry.Provides.Contains(nested.Id, StringComparer.OrdinalIgnoreCase))
                            entry.Provides.Add(nested.Id);
                        // 合并内嵌 Mod 的依赖，使 bundled jar 的前置也能被检测
                        foreach (var (depId, range) in nested.Depends)
                            if (!entry.Depends.ContainsKey(depId))
                                entry.Depends[depId] = range;
                    }
                }
                entry.HasJarInJar = true;
            }
            catch
            {
                // 跳过无法读取的内嵌 jar
            }
        }

        return entry;
    }

    /// <summary>
    /// 解析 Forge / NeoForge 的 mods.toml（NeoForge 为 neoforge.mods.toml）。
    /// </summary>
    private static ModFileEntry? ParseModsToml(string toml, string fileName, string loader = "forge")
    {
        string modId = string.Empty, version = string.Empty, displayName = string.Empty;
        var depends = new Dictionary<string, string>();

        var section = 0; // 0 = 其他, 1 = [[mods]], 2 = [[dependencies.xxx]]
        var curDepId = string.Empty;
        var curDepRange = "*";
        var curDepMandatory = true;

        foreach (var rawLine in toml.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith("[[mods]]", StringComparison.OrdinalIgnoreCase))
            {
                section = 1;
                continue;
            }
            if (line.StartsWith("[[dependencies.", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[dependencies.", StringComparison.OrdinalIgnoreCase))
            {
                // 保存上一个依赖
                CommitTomlDependency(depends, ref curDepId, curDepRange, curDepMandatory);
                section = 2;
                continue;
            }
            if (line.StartsWith('['))
            {
                CommitTomlDependency(depends, ref curDepId, curDepRange, curDepMandatory);
                section = 0;
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var value = ParseTomlValue(line[(eq + 1)..]);

            switch (section)
            {
                case 1:
                    switch (key)
                    {
                        case "modid": modId = value; break;
                        case "version": version = value; break;
                        case "displayname": displayName = value; break;
                    }
                    break;
                case 2:
                    switch (key)
                    {
                        case "modid": curDepId = value; break;
                        case "versionrange": curDepRange = value; break;
                        case "mandatory": curDepMandatory = !value.Equals("false", StringComparison.OrdinalIgnoreCase); break;
                        // type 为 required 才是硬性前置；optional / recommended / discouraged / incompatible 均不算缺失
                        case "type":
                            curDepMandatory = value.Equals("required", StringComparison.OrdinalIgnoreCase);
                            break;
                    }
                    break;
            }
        }
        CommitTomlDependency(depends, ref curDepId, curDepRange, curDepMandatory);

        if (string.IsNullOrEmpty(modId)) return null;
        return new ModFileEntry
        {
            FileName = fileName,
            Id = modId,
            Version = version,
            DisplayName = displayName,
            Loader = loader,
            Depends = depends
        };
    }

    /// <summary>
    /// 解析 TOML 行右侧的值，去除行内注释（如 <c>"id" #mandatory</c>）。
    /// </summary>
    private static string ParseTomlValue(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith('"'))
        {
            // 带引号的字符串：取第一对引号之间的内容
            var end = text.IndexOf('"', 1);
            return end > 0 ? text[1..end] : text.Trim('"');
        }
        // 无引号的值（如 true / false / [1.21.1]）：去掉 # 之后的注释
        var comment = text.IndexOf('#');
        return (comment >= 0 ? text[..comment] : text).Trim();
    }

    private static void CommitTomlDependency(Dictionary<string, string> depends, ref string curDepId,
        string range, bool mandatory)
    {
        if (string.IsNullOrEmpty(curDepId)) return;
        if (mandatory && !depends.ContainsKey(curDepId)) depends[curDepId] = range;
        curDepId = string.Empty;
    }

    /// <summary>
    /// 解析旧版 Forge 的 mcmod.info（JSON）。
    /// </summary>
    private static ModFileEntry? ParseMcModInfo(string json, string fileName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement modElement;
            if (root.ValueKind == JsonValueKind.Array)
                modElement = root.EnumerateArray().FirstOrDefault();
            else if (root.TryGetProperty("modList", out var list) && list.ValueKind == JsonValueKind.Array)
                modElement = list.EnumerateArray().FirstOrDefault();
            else
                return null;

            if (modElement.ValueKind != JsonValueKind.Object) return null;
            var id = modElement.TryGetProperty("modid", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrEmpty(id)) return null;
            var version = modElement.TryGetProperty("version", out var verEl) ? verEl.ToString() : string.Empty;
            var name = modElement.TryGetProperty("displayName", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;

            var depends = new Dictionary<string, string>();
            foreach (var key in new[] { "requiredMods", "dependencies" })
            {
                if (!modElement.TryGetProperty(key, out var depEl)) continue;
                if (depEl.ValueKind == JsonValueKind.String)
                {
                    var depId = depEl.GetString();
                    if (!string.IsNullOrEmpty(depId)) depends[depId] = "*";
                }
                else if (depEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in depEl.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) continue;
                        var depId = item.GetString();
                        if (!string.IsNullOrEmpty(depId)) depends[depId] = "*";
                    }
                }
            }

            return new ModFileEntry
            {
                FileName = fileName,
                Id = id,
                Version = version,
                DisplayName = name,
                Loader = "legacyforge",
                Depends = depends
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 计算缺失的前置依赖（保留旧接口）。
    /// </summary>
    public static List<ModMissingDependency> FindMissingDependencies(string modsPath)
    {
        var result = Scan(modsPath);
        return result.Missing;
    }

    /// <summary>
    /// 从 Modrinth 解析缺失依赖的可安装版本。先以依赖 ID 作为项目 ID/Slug 查询，失败后以关键词搜索。
    /// 均失败时若配置了 CurseForge API Key，则尝试从 CurseForge 获取（部分前置仅在 CurseForge 发布）。
    /// </summary>
    public static async Task<bool> ResolveAsync(ModMissingDependency dep, CancellationToken token = default)
    {
        if (await TryResolveByIdAsync(dep.Id, dep, token).ConfigureAwait(false))
        {
            dep.Source = "modrinth";
            return true;
        }

        // 搜索回退：部分 Mod 的 modId 与 Modrinth 项目 Slug 不一致（如 morecalling ↔ more-culling）
        // 为避免安装错误的模组，仅接受规范化后与依赖 ID 完全一致的候选
        var candidates = await SearchCandidatesAsync(dep.Id, dep, token: token).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            if (!IsCloseMatch(candidate, dep.Id)) continue;
            if (await TryResolveByIdAsync(candidate.ProjectId, dep, token).ConfigureAwait(false))
            {
                dep.ProjectId = candidate.ProjectId;
                dep.ProjectTitle = candidate.Title;
                dep.Source = "modrinth";
                return true;
            }
        }

        // CurseForge 回退：仅在配置了 API Key 时尝试（如 Flywheel 等仅在 CurseForge 发布的前置）
        if (!string.IsNullOrWhiteSpace(CurseForgeApiKey) &&
            await ResolveFromCurseForgeAsync(dep, token).ConfigureAwait(false))
            return true;

        return false;
    }

    /// <summary>
    /// 从 CurseForge 解析缺失依赖。先按关键词搜索项目（严格匹配），再获取匹配游戏版本与加载器的文件。
    /// </summary>
    public static async Task<bool> ResolveFromCurseForgeAsync(ModMissingDependency dep,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(CurseForgeApiKey)) return false;

        // 1. 搜索项目
        var searchUrl = $"{CurseForgeApi}/v1/mods/search?gameId={CurseForgeGameId}&classId={CurseForgeModClassId}" +
                        $"&searchFilter={Uri.EscapeDataString(dep.Id)}" +
                        $"&gameVersion={Uri.EscapeDataString(dep.GameVersion)}&pageSize=10&sortField=Name&sortOrder=asc";
        using var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        searchRequest.Headers.Add("x-api-key", CurseForgeApiKey);
        using var searchResponse = await Client.SendAsync(searchRequest, token).ConfigureAwait(false);
        if (!searchResponse.IsSuccessStatusCode) return false;

        await using var searchStream = await searchResponse.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var searchDoc = await JsonDocument.ParseAsync(searchStream, cancellationToken: token)
            .ConfigureAwait(false);
        if (!searchDoc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var project in data.EnumerateArray())
        {
            var addonId = project.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetInt64()
                : 0;
            var slug = project.TryGetProperty("slug", out var slugEl) ? slugEl.GetString() ?? "" : "";
            var name = project.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            if (addonId == 0) continue;
            // 严格匹配：规范化后与依赖 ID 完全一致，避免下载错误的模组
            if (NormalizeForMatch(slug) != NormalizeForMatch(dep.Id) &&
                NormalizeForMatch(name) != NormalizeForMatch(dep.Id))
                continue;

            // 2. 获取该项目的文件列表，筛选游戏版本
            var filesUrl = $"{CurseForgeApi}/v1/mods/{addonId}/files" +
                           $"?gameVersion={Uri.EscapeDataString(dep.GameVersion)}&pageSize=50";
            using var filesRequest = new HttpRequestMessage(HttpMethod.Get, filesUrl);
            filesRequest.Headers.Add("x-api-key", CurseForgeApiKey);
            using var filesResponse = await Client.SendAsync(filesRequest, token).ConfigureAwait(false);
            if (!filesResponse.IsSuccessStatusCode) continue;

            await using var filesStream = await filesResponse.Content.ReadAsStreamAsync(token)
                .ConfigureAwait(false);
            using var filesDoc = await JsonDocument.ParseAsync(filesStream, cancellationToken: token)
                .ConfigureAwait(false);
            if (!filesDoc.RootElement.TryGetProperty("data", out var files) ||
                files.ValueKind != JsonValueKind.Array)
                continue;

            // 优先选择声明了目标加载器的文件，否则取第一个
            JsonElement bestFile = default;
            var bestIsLoaderMatch = false;
            foreach (var file in files.EnumerateArray())
            {
                var isLoaderMatch = false;
                if (file.TryGetProperty("gameVersions", out var gvs) && gvs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var gv in gvs.EnumerateArray())
                    {
                        if (gv.ValueKind != JsonValueKind.String) continue;
                        if (gv.GetString()?.Equals(CurseForgeLoaderName(dep.Loader),
                                StringComparison.OrdinalIgnoreCase) == true)
                        {
                            isLoaderMatch = true;
                            break;
                        }
                    }
                }
                if (isLoaderMatch)
                {
                    bestFile = file;
                    bestIsLoaderMatch = true;
                    break;
                }
                if (!bestIsLoaderMatch && bestFile.ValueKind == JsonValueKind.Undefined)
                    bestFile = file;
            }
            if (bestFile.ValueKind != JsonValueKind.Object) continue;

            var fileName = bestFile.TryGetProperty("fileName", out var fnEl) ? fnEl.GetString() : null;
            var downloadUrl = bestFile.TryGetProperty("downloadUrl", out var duEl) ? duEl.GetString() : null;
            var fileId = bestFile.TryGetProperty("id", out var fidEl) && fidEl.ValueKind == JsonValueKind.Number
                ? fidEl.GetInt64()
                : 0;
            if (string.IsNullOrEmpty(fileName) || fileId == 0) continue;
            // downloadUrl 偶有为 null 的情况，此时按已知 CDN 路径规律拼接
            if (string.IsNullOrEmpty(downloadUrl))
                downloadUrl = $"https://media.forgecdn.net/files/{fileId / 1000}/{fileId % 1000}/" +
                              $"{Uri.EscapeDataString(fileName)}";

            dep.FileName = fileName;
            dep.DownloadUrl = downloadUrl;
            dep.VersionNumber = bestFile.TryGetProperty("displayName", out var dnEl) ? dnEl.GetString() : null;
            dep.ProjectId = addonId.ToString();
            dep.ProjectTitle = name;
            dep.Source = "curseforge";
            return true;
        }

        return false;
    }

    /// <summary>
    /// 将内部加载器标识转换为 CurseForge 文件标签名。
    /// </summary>
    private static string CurseForgeLoaderName(string loader)
    {
        return loader switch
        {
            "fabric" => "Fabric",
            "quilt" => "Quilt",
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            "legacyforge" => "Forge",
            _ => loader
        };
    }

    /// <summary>
    /// 判断搜索结果是否与依赖 ID 足够匹配：规范化（小写并去除非字母数字字符）后完全一致。
    /// </summary>
    private static bool IsCloseMatch(ModFixCandidate candidate, string keyword)
    {
        var key = NormalizeForMatch(keyword);
        if (key.Length == 0) return false;
        return NormalizeForMatch(candidate.Slug) == key || NormalizeForMatch(candidate.Title) == key;
    }

    private static string NormalizeForMatch(string text)
    {
        return new string((text ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    private static async Task<bool> TryResolveByIdAsync(string projectId, ModMissingDependency dep,
        CancellationToken token)
    {
        // 依次放宽筛选条件：加载器+游戏版本 → 仅游戏版本 → 仅加载器
        var filters = new[]
        {
            $"?loaders=[\"{dep.Loader}\"]&game_versions=[\"{Uri.EscapeDataString(dep.GameVersion)}\"]",
            $"?game_versions=[\"{Uri.EscapeDataString(dep.GameVersion)}\"]",
            $"?loaders=[\"{dep.Loader}\"]"
        };

        foreach (var filter in filters)
        {
            var url = $"{ModrinthApi}/project/{Uri.EscapeDataString(projectId)}/version{filter}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", UserAgent);
            try
            {
                using var response = await Client.SendAsync(request, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;

                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                    continue;

                foreach (var version in doc.RootElement.EnumerateArray())
                {
                    if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                        continue;
                    var file = files.EnumerateArray().FirstOrDefault();
                    if (file.ValueKind != JsonValueKind.Object) continue;
                    if (!file.TryGetProperty("url", out var urlEl)) continue;
                    dep.FileName = file.TryGetProperty("filename", out var fnEl) ? fnEl.GetString() : null;
                    dep.DownloadUrl = urlEl.GetString();
                    dep.VersionNumber = version.TryGetProperty("version_number", out var vnEl)
                        ? vnEl.GetString()
                        : null;
                    dep.ProjectId = projectId;
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 尝试下一组筛选条件
            }
        }
        return false;
    }

    /// <summary>
    /// 在 Modrinth 上搜索与依赖 ID 相关的项目。
    /// </summary>
    public static async Task<List<ModFixCandidate>> SearchCandidatesAsync(string keyword,
        ModMissingDependency dep, int limit = 8, CancellationToken token = default)
    {
        var result = new List<ModFixCandidate>();
        var facets = $"[[\"versions:{dep.GameVersion}\"],[\"categories:{dep.Loader}\"]]";
        var url = $"{ModrinthApi}/search?query={Uri.EscapeDataString(keyword)}&limit={limit}" +
                  $"&facets={Uri.EscapeDataString(facets)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", UserAgent);
        try
        {
            using var response = await Client.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return result;

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var hit in hits.EnumerateArray())
            {
                result.Add(new ModFixCandidate
                {
                    ProjectId = hit.TryGetProperty("project_id", out var idEl) ? idEl.GetString() ?? "" : "",
                    Slug = hit.TryGetProperty("slug", out var slugEl) ? slugEl.GetString() ?? "" : "",
                    Title = hit.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "",
                    Description = hit.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? "" : ""
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 搜索失败时返回空列表
        }
        return result;
    }

    /// <summary>
    /// 下载并安装已解析的依赖到 mods 目录。
    /// </summary>
    public static async Task<bool> InstallAsync(ModMissingDependency dep, string modsPath,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(dep.DownloadUrl) || string.IsNullOrEmpty(dep.FileName)) return false;
        Directory.CreateDirectory(modsPath);
        var target = Path.Combine(modsPath, dep.FileName);
        if (File.Exists(target))
        {
            progress?.Report(1d);
            return true;
        }

        var temp = target + ".tmp";
        using var request = new HttpRequestMessage(HttpMethod.Get, dep.DownloadUrl);
        request.Headers.Add("User-Agent", UserAgent);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var contentStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var fileStream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long read = 0;
        while (true)
        {
            var count = await contentStream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count <= 0) break;
            await fileStream.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            read += count;
            if (total > 0) progress?.Report(Math.Min(1d, (double)read / total));
        }

        progress?.Report(1d);
        await fileStream.FlushAsync(token).ConfigureAwait(false);
        fileStream.Close();
        if (File.Exists(target)) File.Delete(target);
        File.Move(temp, target);
        return true;
    }

    /// <summary>
    /// 生成纯文本检测报告，用于复制到剪贴板。
    /// </summary>
    public static string FormatReport(ModScanResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Mods 目录: {result.ModsPath}");
        sb.AppendLine($"游戏版本: {result.GameVersion}    加载器: {result.Loader}");
        sb.AppendLine($"Mod 总数: {result.Mods.Count}    缺失前置: {result.Missing.Count}");
        if (result.Duplicates.Count > 0)
            sb.AppendLine($"重复 Mod: {string.Join("; ", result.Duplicates)}");
        sb.AppendLine();

        if (result.Missing.Count == 0)
        {
            sb.AppendLine("未检测到缺失的前置依赖。");
        }
        else
        {
            sb.AppendLine("缺失的前置依赖:");
            foreach (var dep in result.Missing)
                sb.AppendLine($"- {dep.Id} (需要者: {dep.RequiredBy}, 版本范围: {dep.VersionRange})" +
                              (string.IsNullOrEmpty(dep.DownloadUrl)
                                  ? ""
                                  : $" [来源: {dep.Source}, 版本: {dep.VersionNumber ?? "?"}]"));
        }

        if (result.Unrecognized.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"无法识别元数据的文件: {string.Join(", ", result.Unrecognized)}");
        }

        sb.AppendLine();
        sb.AppendLine($"扫描时间: {result.ScannedAt:yyyy-MM-dd HH:mm:ss}");
        return sb.ToString();
    }
}
