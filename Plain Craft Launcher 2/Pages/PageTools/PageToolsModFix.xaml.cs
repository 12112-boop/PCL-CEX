using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft;
using PCL.Core.UI;

namespace PCL;

public partial class PageToolsModFix
{
    /// <summary>
    ///     自定义 mods 目录模式下的路径。
    /// </summary>
    private string _customModsPath = string.Empty;

    /// <summary>
    ///     最近一次扫描的结果。
    /// </summary>
    private ModScanResult _scanResult = null!;

    public PageToolsModFix()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    /// <summary>
    ///     刷新页面：重新枚举实例列表。
    /// </summary>
    public void Reload()
    {
        try
        {
            var previous = CmbInstance.SelectedItem as string;
            CmbInstance.Items.Clear();
            var instances = ModModPatch.ListInstances();
            foreach (var instance in instances) CmbInstance.Items.Add(instance);

            if (instances.Count == 0)
            {
                LabInfo.Text = Lang.Text("Tools.ModFix.Select.None");
                BtnScan.IsEnabled = false;
            }
            else
            {
                if (previous is not null && instances.Contains(previous))
                    CmbInstance.SelectedItem = previous;
                else
                    CmbInstance.SelectedIndex = 0;
                BtnScan.IsEnabled = true;
            }
            UpdateInfo();
            LoadCurseForgeKey();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "Mod 前置修复工具：刷新实例列表失败", ModBase.LogLevel.Hint);
        }
    }

    /// <summary>
    ///     从配置加载 CurseForge API Key。
    /// </summary>
    private void LoadCurseForgeKey()
    {
        try
        {
            var key = Config.Tool.ModFixCurseForgeKey ?? string.Empty;
            ModModPatch.CurseForgeApiKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
            if (!string.IsNullOrWhiteSpace(key))
                TextCurseForgeKey.Text = key.Trim();
            UpdateKeyStatus();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "Mod 前置修复工具：加载 CurseForge API Key 失败", ModBase.LogLevel.Hint);
        }
    }

    private void UpdateKeyStatus()
    {
        LabKeyStatus.Text = string.IsNullOrWhiteSpace(ModModPatch.CurseForgeApiKey)
            ? Lang.Text("Tools.ModFix.CurseForge.NotConfigured")
            : Lang.Text("Tools.ModFix.CurseForge.Configured");
    }

    /// <summary>
    ///     保存 CurseForge API Key 到配置（加密存储）。
    /// </summary>
    private void TextCurseForgeKey_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        try
        {
            var key = TextCurseForgeKey.Text?.Trim() ?? string.Empty;
            Config.Tool.ModFixCurseForgeKey = key;
            ModModPatch.CurseForgeApiKey = string.IsNullOrWhiteSpace(key) ? null : key;
            UpdateKeyStatus();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "Mod 前置修复工具：保存 CurseForge API Key 失败", ModBase.LogLevel.Hint);
        }
    }

    /// <summary>
    ///     打开 CurseForge 开发者控制台以免费申请 API Key。
    /// </summary>
    private void BtnGetKey_Click(object sender, MouseButtonEventArgs e)
    {
        ModBase.OpenWebsite("https://console.curseforge.com/");
    }

    /// <summary>
    ///     当前选择的 mods 目录。自定义模式返回自定义路径，否则返回实例的 mods 目录。
    /// </summary>
    private string CurrentModsPath()
    {
        if (!string.IsNullOrEmpty(_customModsPath)) return _customModsPath;
        var instance = CmbInstance.SelectedItem as string;
        if (string.IsNullOrEmpty(instance)) return string.Empty;
        return ModModPatch.GetModsPath(instance);
    }

    /// <summary>
    ///     当前选择的实例名（自定义模式返回空）。
    /// </summary>
    private string CurrentInstanceName()
    {
        if (!string.IsNullOrEmpty(_customModsPath)) return string.Empty;
        return CmbInstance.SelectedItem as string ?? string.Empty;
    }

    private void UpdateInfo()
    {
        var instance = CurrentInstanceName();
        var modsPath = CurrentModsPath();
        if (string.IsNullOrEmpty(modsPath)) return;

        if (!string.IsNullOrEmpty(instance))
        {
            var (loader, gameVersion) = ModModPatch.DetectLoaderAndGameVersion(instance);
            LabInfo.Text = Lang.Text("Tools.ModFix.Info", gameVersion, GetLoaderDisplayName(loader), modsPath);
        }
        else
        {
            LabInfo.Text = Lang.Text("Tools.ModFix.Info.Custom", modsPath);
        }
    }

    private static string GetLoaderDisplayName(string loader)
    {
        return loader switch
        {
            "fabric" => "Fabric",
            "quilt" => "Quilt",
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            "legacyforge" => "Forge (Legacy)",
            _ => string.IsNullOrEmpty(loader) ? Lang.Text("Tools.ModFix.Loader.Unknown") : loader
        };
    }

    private void CmbInstance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateInfo();
    }

    /// <summary>
    ///     浏览并选择自定义 mods 目录。
    /// </summary>
    private void BtnBrowse_Click(object sender, EventArgs e)
    {
        var folder = SystemDialogs.SelectFolder(Lang.Text("Tools.ModFix.Select.BrowseTitle"));
        if (string.IsNullOrEmpty(folder)) return;
        _customModsPath = folder.TrimEnd('\\', '/');
        CmbInstance.Items.Clear();
        CmbInstance.Items.Add(Lang.Text("Tools.ModFix.Select.CustomItem", _customModsPath));
        CmbInstance.SelectedIndex = 0;
        BtnScan.IsEnabled = true;
        UpdateInfo();
    }

    /// <summary>
    ///     开始扫描 mods 目录。
    /// </summary>
    private void BtnScan_Click(object sender, MouseButtonEventArgs e)
    {
        var modsPath = CurrentModsPath();
        var instanceName = CurrentInstanceName();
        if (string.IsNullOrEmpty(modsPath))
        {
            HintService.Hint(Lang.Text("Tools.ModFix.Error.NoModsPath"), HintType.Error);
            return;
        }

        SetBusy(true);
        LabStatus.Visibility = Visibility.Visible;
        LabStatus.Text = Lang.Text("Tools.ModFix.Status.Scanning");

        // 注意：必须在后台线程开始前读取 UI 状态，避免跨线程访问控件
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var (loader, gameVersion) = string.IsNullOrEmpty(instanceName)
                    ? (string.Empty, string.Empty)
                    : ModModPatch.DetectLoaderAndGameVersion(instanceName);
                var result = ModModPatch.Scan(modsPath, loader, gameVersion);
                ModBase.RunInUiWait(() =>
                {
                    _scanResult = result;
                    RefreshResults();
                    LabStatus.Visibility = Visibility.Collapsed;
                    SetBusy(false);
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "Mod 前置修复工具：扫描失败", ModBase.LogLevel.Hint);
                ModBase.RunInUi(() =>
                {
                    LabStatus.Text = Lang.Text("Tools.ModFix.Error.ScanFailed");
                    SetBusy(false);
                });
            }
        }, "Mod Fix Scan");
    }

    /// <summary>
    ///     根据扫描结果刷新列表。缺失前置以勾选框呈现，由用户决定安装哪些。
    /// </summary>
    private void RefreshResults()
    {
        var result = _scanResult;
        PanResults.Children.Clear();
        if (result is null) return;

        LabInfo.Text = string.IsNullOrEmpty(CurrentInstanceName())
            ? Lang.Text("Tools.ModFix.Info.Custom", result.ModsPath)
            : Lang.Text("Tools.ModFix.Info", result.GameVersion, GetLoaderDisplayName(result.Loader),
                result.ModsPath);

        // 汇总信息
        var summary = Lang.Text("Tools.ModFix.Result.Summary", result.Mods.Count, result.Missing.Count,
            result.Duplicates.Count);
        if (result.Missing.Count == 0 && result.Duplicates.Count == 0)
        {
            summary += Environment.NewLine + Lang.Text("Tools.ModFix.Result.AllGood");
        }
        else
        {
            summary += Environment.NewLine + Lang.Text("Tools.ModFix.Result.ClickHint");
        }
        LabSummary.Text = summary;

        // 缺失前置（可勾选，默认全选）
        foreach (var dep in result.Missing)
        {
            var row = new MyListItem
            {
                Title = dep.Id,
                Info = Lang.Text("Tools.ModFix.Result.MissingInfo", dep.RequiredBy, dep.VersionRange),
                Type = MyListItem.CheckType.CheckBox,
                Checked = true,
                LogoScale = 0.9,
                SvgIcon = "lucide/puzzle",
                Tag = dep,
                Margin = new Thickness(0, 0, 0, 8)
            };
            row.Changed += ResultRow_Changed;
            PanResults.Children.Add(row);
        }

        // 重复 Mod（仅展示，不可勾选）
        foreach (var duplicate in result.Duplicates)
            PanResults.Children.Add(new MyListItem
            {
                Title = duplicate.Split(" ← ")[0],
                Info = Lang.Text("Tools.ModFix.Result.DuplicateInfo", duplicate.Split(" ← ").Last()),
                Type = MyListItem.CheckType.None,
                LogoScale = 0.9,
                SvgIcon = "lucide/copy",
                Margin = new Thickness(0, 0, 0, 8)
            });

        // 无法识别的文件（仅展示）
        foreach (var unrecognized in result.Unrecognized)
            PanResults.Children.Add(new MyListItem
            {
                Title = unrecognized,
                Info = Lang.Text("Tools.ModFix.Result.UnrecognizedInfo"),
                Type = MyListItem.CheckType.None,
                LogoScale = 0.9,
                SvgIcon = "lucide/circle-help",
                Margin = new Thickness(0, 0, 0, 8)
            });

        CardAction.Visibility = result.Missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // 有内容时自动展开结果卡片
        if (PanResults.Children.Count > 0) CardResult.IsSwapped = false;
    }

    /// <summary>
    ///     勾选状态变化：仅记录日志，实际选择在修复时读取。
    /// </summary>
    private void ResultRow_Changed(object sender, ModBase.RouteEventArgs e)
    {
        if (sender is MyListItem { Tag: ModMissingDependency dep } item)
            ModBase.Log($"[ModFix] 前置勾选状态变化：{dep.Id} -> {item.Checked}");
    }

    /// <summary>
    ///     获取当前勾选的缺失前置列表。
    /// </summary>
    private List<ModMissingDependency> GetSelectedDependencies()
    {
        var selected = new List<ModMissingDependency>();
        foreach (var child in PanResults.Children)
        {
            if (child is MyListItem { Checked: true, Tag: ModMissingDependency dep })
                selected.Add(dep);
        }
        return selected;
    }

    /// <summary>
    ///     自动修复全部缺失前置。
    /// </summary>
    private void BtnFixAll_Click(object sender, MouseButtonEventArgs e)
    {
        if (_scanResult is null || _scanResult.Missing.Count == 0) return;
        var modsPath = CurrentModsPath();
        if (string.IsNullOrEmpty(modsPath))
        {
            HintService.Hint(Lang.Text("Tools.ModFix.Error.NoModsPath"), HintType.Error);
            return;
        }

        // 仅修复用户勾选的前置，避免安装不必要的模组
        var pending = GetSelectedDependencies();
        if (pending.Count == 0)
        {
            HintService.Hint(Lang.Text("Tools.ModFix.Fix.NoneSelected"), HintType.Warning);
            return;
        }

        // 确认弹窗中列出将要安装的完整清单
        var confirmMessage = Lang.Text("Tools.ModFix.Fix.Confirm", pending.Count,
            string.Join(Environment.NewLine, pending.Select(d => $"- {d.Id} [{d.VersionRange}]")));
        if (ModMain.MyMsgBox(
                confirmMessage,
                Lang.Text("Tools.ModFix.Fix.ConfirmTitle"),
                Lang.Text("Common.Action.Continue"),
                Lang.Text("Common.Action.Cancel")) == 2)
            return;

        SetBusy(true);
        LabStatus.Visibility = Visibility.Visible;

        ModBase.RunInNewThread(async () =>
        {
            var fixedDeps = new List<ModMissingDependency>();
            var failedDeps = new List<ModMissingDependency>();
            for (var i = 0; i < pending.Count; i++)
            {
                var dep = pending[i];
                ModBase.RunInUi(() =>
                    LabStatus.Text = Lang.Text("Tools.ModFix.Status.Fixing", i + 1, pending.Count, dep.Id));
                try
                {
                    if (await FixDependencyAsync(dep, modsPath))
                        fixedDeps.Add(dep);
                    else
                        failedDeps.Add(dep);
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, $"Mod 前置修复工具：修复 {dep.Id} 失败", ModBase.LogLevel.Hint);
                    failedDeps.Add(dep);
                }
            }

            ModBase.RunInUiWait(() =>
            {
                LabStatus.Visibility = Visibility.Collapsed;
                SetBusy(false);
                ShowFixResult(fixedDeps, failedDeps);
                RescanQuiet();
            });
        }, "Mod Fix All");
    }

    /// <summary>
    ///     解析并安装单个缺失前置。返回是否成功。
    /// </summary>
    private static async System.Threading.Tasks.Task<bool> FixDependencyAsync(ModMissingDependency dep,
        string modsPath)
    {
        if (!await ModModPatch.ResolveAsync(dep).ConfigureAwait(false)) return false;
        return await ModModPatch.InstallAsync(dep, modsPath).ConfigureAwait(false);
    }

    /// <summary>
    ///     显示修复结果。
    /// </summary>
    private void ShowFixResult(List<ModMissingDependency> fixedDeps, List<ModMissingDependency> failedDeps)
    {
        var message = Lang.Text("Tools.ModFix.Fix.Finished", fixedDeps.Count, failedDeps.Count);
        if (failedDeps.Count > 0)
        {
            message += Environment.NewLine + Environment.NewLine +
                       Lang.Text("Tools.ModFix.Fix.FailedList", string.Join(", ", failedDeps.Select(d => d.Id)));
            if (string.IsNullOrWhiteSpace(ModModPatch.CurseForgeApiKey))
                message += Environment.NewLine + Lang.Text("Tools.ModFix.CurseForge.HintInResult");
        }
        ModMain.MyMsgBox(message, Lang.Text("Tools.ModFix.Fix.FinishedTitle"),
            Lang.Text("Common.Action.Confirm"), "", "", false, true, true);
        HintService.Hint(Lang.Text("Tools.ModFix.Fix.Done", fixedDeps.Count, failedDeps.Count),
            failedDeps.Count == 0 ? HintType.Success : HintType.Warning);
    }

    /// <summary>
    ///     静默重新扫描以刷新结果。
    /// </summary>
    private void RescanQuiet()
    {
        var modsPath = CurrentModsPath();
        var instanceName = CurrentInstanceName();
        if (string.IsNullOrEmpty(modsPath)) return;
        // 注意：必须在后台线程开始前读取 UI 状态，避免跨线程访问控件
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var (loader, gameVersion) = string.IsNullOrEmpty(instanceName)
                    ? (string.Empty, string.Empty)
                    : ModModPatch.DetectLoaderAndGameVersion(instanceName);
                var result = ModModPatch.Scan(modsPath, loader, gameVersion);
                ModBase.RunInUi(() =>
                {
                    if (_scanResult is null) return;
                    _scanResult = result;
                    RefreshResults();
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "Mod 前置修复工具：重新扫描失败", ModBase.LogLevel.Hint);
            }
        }, "Mod Fix Rescan");
    }

    /// <summary>
    ///     打开 mods 目录。
    /// </summary>
    private void BtnOpenFolder_Click(object sender, MouseButtonEventArgs e)
    {
        var modsPath = CurrentModsPath();
        if (string.IsNullOrEmpty(modsPath)) return;
        try
        {
            if (!Directory.Exists(modsPath)) Directory.CreateDirectory(modsPath);
            ModBase.OpenExplorer(modsPath);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "Mod 前置修复工具：打开 mods 目录失败", ModBase.LogLevel.Hint);
        }
    }

    /// <summary>
    ///     复制检测报告。
    /// </summary>
    private void BtnCopyReport_Click(object sender, MouseButtonEventArgs e)
    {
        if (_scanResult is null)
        {
            HintService.Hint(Lang.Text("Tools.ModFix.Error.NoScan"));
            return;
        }
        ModBase.ClipboardSet(ModModPatch.FormatReport(_scanResult));
    }

    /// <summary>
    ///     设置忙碌状态。
    /// </summary>
    private void SetBusy(bool busy)
    {
        BtnScan.IsEnabled = !busy;
        BtnFixAll.IsEnabled = !busy;
        CmbInstance.IsEnabled = !busy;
    }
}
