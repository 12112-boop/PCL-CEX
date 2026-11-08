using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

public partial class PageSkinMine : MyPageRight
{
    public PageSkinMine()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadList();
    }

    public void Reload() => LoadList();

    private void LoadList()
    {
        PanSkinList.Children.Clear();
        var list = ModSkinStore.ListStored();
        if (list.Count == 0)
        {
            HintEmpty.Visibility = Visibility.Visible;
            return;
        }

        foreach (var s in list)
        {
            PanSkinList.Children.Add(BuildSkinCard(s));
        }
    }

    private MyCard BuildSkinCard(ModSkinStore.StoredSkin s)
    {
        var card = new MyCard
        {
            Margin = new Thickness(0, 0, 15, 15),
            MinWidth = 130,
            MinHeight = 100,
            ToolTip = s.Author,
            // 关闭折叠机制，让卡片始终展开，「应用/删除」按钮始终可见
            CanSwap = false
        };
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10) };

        var img = new System.Windows.Controls.Image
        {
            Width = 64,
            Height = 64,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 0, 6)
        };
        try
        {
            var bmp = new System.Drawing.Bitmap(s.LocalFile);
            var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            bmp.Dispose();
            ms.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            img.Source = bi;
        }
        catch
        {
            // 图片损坏，留白
        }

        var nameBlock = new TextBlock
        {
            Text = s.Name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 110
        };

        var applyBtn = new MyButton
        {
            Text = Lang.Text("Skin.Mine.Apply"),
            MinWidth = 70,
            Height = 28,
            Margin = new Thickness(0, 4, 4, 0),
            Tag = s,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        applyBtn.Click += (sender, _) =>
        {
            var tag = ((MyButton)sender).Tag as ModSkinStore.StoredSkin;
            if (tag is null) return;
            ApplySkin(tag);
        };

        var delBtn = new MyIconButton
        {
            ToolTip = Lang.Text("Skin.Mine.Delete"),
            SvgIcon = "lucide/trash-2",
            LogoScale = 0.8,
            Tag = s
        };
        delBtn.Click += (sender, _) =>
        {
            var tag = ((MyIconButton)sender).Tag as ModSkinStore.StoredSkin;
            if (tag is null) return;
            DeleteSkin(tag);
        };

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };
        btnPanel.Children.Add(applyBtn);
        btnPanel.Children.Add(delBtn);

        panel.Children.Add(img);
        panel.Children.Add(nameBlock);
        panel.Children.Add(btnPanel);
        card.Children.Add(panel);
        return card;
    }

    private void ApplySkin(ModSkinStore.StoredSkin s)
    {
        ModBase.RunInNewThread(() =>
        {
            try
            {
                ModSkinStore.ValidateSkin(s.LocalFile);
                var isSlim = s.Name.Contains("slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic";
                ModSkinStore.ApplyStored(s.LocalFile, isSlim);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "应用皮肤失败", ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Skin.Mine.ApplyError", ex.Message));
                ModBase.RunInUi(() =>
                    HintService.Hint(Lang.Text("Skin.Mine.ApplyError", ex.Message), HintType.Error));
            }
        }, "Skin Apply " + s.Name);
    }

    private void DeleteSkin(ModSkinStore.StoredSkin s)
    {
        var confirm = ModMain.MyMsgBox(
            Lang.Text("Skin.Mine.DeleteConfirm", s.Name),
            Lang.Text("Common.Action.Delete"),
            Lang.Text("Common.Action.Confirm"),
            Lang.Text("Common.Action.Cancel"));
        if (confirm != 1) return;
        ModSkinStore.DeleteStored(s.LocalFile);
        HintService.Hint(Lang.Text("Skin.Mine.DeleteSuccess", s.Name), HintType.Success);
        LoadList();
    }

    private void BtnImport_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var file = SystemDialogs.SelectFile(Lang.Text("Skin.Mine.Import.Filter"), Lang.Text("Skin.Mine.Import.Title"));
        if (string.IsNullOrEmpty(file)) return;
        var name = Path.GetFileNameWithoutExtension(file);
        ModBase.RunInNewThread(() =>
        {
            try
            {
                ModSkinStore.ImportLocal(file, name);
                ModBase.RunInUi(() =>
                {
                    HintService.Hint(Lang.Text("Skin.Mine.ImportSuccess", name), HintType.Success);
                    LoadList();
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "导入皮肤失败", ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Skin.Mine.ImportError", ex.Message));
                ModBase.RunInUi(() =>
                    HintService.Hint(Lang.Text("Skin.Mine.ImportError", ex.Message), HintType.Error));
            }
        }, "Skin Import");
    }

    private void BtnOpenFolder_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var folder = Path.Combine(ModBase.pathTemp, "Cache", "LittleSkin");
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }
}
