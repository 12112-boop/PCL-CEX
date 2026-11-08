using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Core.UI;
using PCL.Core.Utils;

namespace PCL;

/// <summary>
///     小皮肤（littleskin.cn）皮肤库访问，与本地「我的皮肤」存储。
/// </summary>
public static class ModSkinStore
{
    public class SkinItem
    {
        public string Id; // 小皮肤 tid
        public string Name;
        public string Avatar; // 小皮肤头像/预览图 URL
        public string Author;
        public string RawUrl => "https://littleskin.cn/raw/" + Id;
        public string PreviewUrl => Avatar + "/64";
    }

    public class StoredSkin
    {
        public string Name;
        public string LocalFile; // 本地文件绝对路径
        public string Author;
        public long AddedAt;
    }

    private static readonly string storeFolder =
        Path.Combine(ModBase.pathTemp, "Cache", "LittleSkin");

    /// <summary>
    ///     下载指定小皮肤到「我的皮肤」存储。
    /// </summary>
    public static StoredSkin DownloadSkin(string tid, string name, string author)
    {
        return DownloadSkinFromUrl("https://littleskin.cn/raw/" + tid, name, author);
    }

    /// <summary>
    ///     从任意 URL 下载皮肤文件到「我的皮肤」存储（小皮肤站下载被 WebView2 拦截时调用）。
    /// </summary>
    public static StoredSkin DownloadSkinFromUrl(string url, string fileName, string author = "")
    {
        Directory.CreateDirectory(storeFolder);
        var safeName = string.IsNullOrEmpty(fileName) ? ModBase.GetHash(url).ToString() : SanitizeFileName(fileName);
        if (!safeName.EndsWithF(".png", true)) safeName += ".png";
        var file = Path.Combine(storeFolder, safeName);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.Timeout = TimeSpan.FromSeconds(30);
        var bytes = client.GetByteArrayAsync(url).GetAwaiter().GetResult();
        File.WriteAllBytes(file, bytes);
        // 校验
        ValidateSkin(file);
        return new StoredSkin
        {
            Name = Path.GetFileNameWithoutExtension(safeName),
            Author = author,
            LocalFile = file,
            AddedAt = File.GetLastWriteTimeUtc(file).Ticks
        };
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString();
    }

    /// <summary>
    ///     校验皮肤文件：64x32 或 64x64，且 ≤ 24KB。
    /// </summary>
    public static void ValidateSkin(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists)
            throw new FileNotFoundException(Lang.Text("Skin.Error.FileMissing"), file);
        if (info.Length > 24 * 1024)
            throw new InvalidDataException(Lang.Text("Skin.Error.FileTooLarge", info.Length / 1024d, "N2"));
        var bmp = new System.Drawing.Bitmap(file);
        if (bmp.Width != 64 || !(bmp.Height == 32 || bmp.Height == 64))
            throw new InvalidDataException(Lang.Text("Skin.Error.InvalidSize"));
        bmp.Dispose();
    }

    /// <summary>
    ///     列出「我的皮肤」。
    /// </summary>
    public static List<StoredSkin> ListStored()
    {
        var result = new List<StoredSkin>();
        if (!Directory.Exists(storeFolder))
            return result;
        foreach (var f in Directory.EnumerateFiles(storeFolder, "*.png"))
        {
            try
            {
                var metaFile = f + ".json";
                string name = Path.GetFileNameWithoutExtension(f);
                string author = "";
                if (File.Exists(metaFile))
                {
                    var meta = (JsonObject)ModBase.GetJson(File.ReadAllText(metaFile));
                    name = meta["name"]?.ToString() ?? name;
                    author = meta["author"]?.ToString() ?? "";
                }
                result.Add(new StoredSkin
                {
                    Name = name,
                    Author = author,
                    LocalFile = f,
                    AddedAt = File.GetLastWriteTimeUtc(f).Ticks
                });
            }
            catch
            {
                // 忽略损坏条目
            }
        }
        return result;
    }

    /// <summary>
    ///     删除「我的皮肤」中的一个条目。
    /// </summary>
    public static void DeleteStored(string localFile)
    {
        var metaFile = localFile + ".json";
        if (File.Exists(localFile)) File.Delete(localFile);
        if (File.Exists(metaFile)) File.Delete(metaFile);
    }

    /// <summary>
    ///     导入本地皮肤文件到「我的皮肤」。
    /// </summary>
    public static StoredSkin ImportLocal(string localFile, string name, string author = "")
    {
        Directory.CreateDirectory(storeFolder);
        var tid = ModBase.GetHash(localFile + name);
        var dest = Path.Combine(storeFolder, tid + ".png");
        var meta = new JsonObject
        {
            ["name"] = name,
            ["author"] = author
        };
        File.Copy(localFile, dest, true);
        File.WriteAllText(dest + ".json", meta.ToJsonString());
        ValidateSkin(dest);
        return new StoredSkin
        {
            Name = name,
            Author = author,
            LocalFile = dest,
            AddedAt = File.GetLastWriteTimeUtc(dest).Ticks
        };
    }

    /// <summary>
    ///     把皮肤应用到当前账号（三种类型）。
    /// </summary>
    public static void ApplyStored(string localFile, string isSlim)
    {
        var profile = ProfileService.Current;
        if (profile is null)
        {
            HintService.Hint(Lang.Text("Skin.Error.NoProfile"), HintType.Error);
            return;
        }

        switch (profile.ProfileType)
        {
            case ProfileType.Microsoft:
                ProfileUi.ChangeSkinMs(localFile, isSlim);
                break;
            case ProfileType.Offline:
            case ProfileType.Authlib:
            case ProfileType.YggdrasilConnect:
                {
                    // 离线/第三方：写入游戏 Skins 目录，本地生效
                    var skinDir = Path.Combine(ModBase.pathPure, "Skins");
                    Directory.CreateDirectory(skinDir);
                    var target = Path.Combine(skinDir, ModSkin.McSkinSex(profile.Uuid) + ".png");
                    File.Copy(localFile, target, true);
                    MySkin.ReloadCache(localFile, profile.Uuid);
                    HintService.Hint(
                        profile.ProfileType == ProfileType.Offline
                            ? Lang.Text("Skin.Apply.OfflineSuccess")
                            : Lang.Text("Skin.Apply.ThirdPartySuccess"),
                        HintType.Success);
                    break;
                }
        }
    }
}
