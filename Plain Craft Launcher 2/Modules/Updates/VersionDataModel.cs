namespace PCL;

public class VersionDataModel
{
    public string Changelog { get; set; }

    /// <summary>更新文件（exe）的直链。仅部分更新源提供。</summary>
    public string DownloadUrl { get; set; }

    public string Sha256 { get; set; }
    public string Source { get; set; }
    public int VersionCode { get; set; }
    public string VersionName { get; set; }
}
