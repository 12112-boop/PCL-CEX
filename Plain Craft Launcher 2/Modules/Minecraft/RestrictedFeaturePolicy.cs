namespace PCL;

/// <summary>
///     功能可用性检查。当前版本不再按国家或地区限制下载、补全与整合包安装等功能。
/// </summary>
internal static class RestrictedFeaturePolicy
{
    internal static bool IsRestricted => false;

    internal static bool Check(string feature, bool showHint = true) => true;

    internal static void ThrowIfRestricted(string feature)
    {
    }
}
