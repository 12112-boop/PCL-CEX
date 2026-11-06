using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using PCL.Core.App;

namespace PCL.Core.UI.Theme;

/// <summary>
/// 多窗口色相注册表。<br/>
/// 各个窗口启动时在此登记自己使用的色相，新窗口据此分配一个与其它窗口都不同的色相，
/// 便于用户区分多个窗口。数据存放在共享目录中，供所有进程读写；已退出进程的残留项会在每次读写时自动清理。
/// </summary>
public static class WindowColorRegistry
{
    private static readonly string _FilePath = Path.Combine(Paths.SharedLocalData, "window-hues.json");

    /// <summary>
    /// 自动分配的备选色相，按顺序取用，避开已注册的色相。
    /// </summary>
    private static readonly int[] _Palette = [300, 150, 30, 210, 90, 330, 270, 60, 180, 0, 240, 120];

    private static readonly JsonSerializerOptions _JsonOptions = new() { WriteIndented = false };

    private sealed class Entry
    {
        public int Pid { get; set; }
        public int Hue { get; set; }
    }

    /// <summary>
    /// 登记当前进程使用的色相（主窗口在主题变化时也应调用以保持注册信息最新）。
    /// </summary>
    /// <param name="hue">色相，取值 0~360</param>
    public static void RegisterHue(int hue)
    {
        _Modify(entries =>
        {
            var result = _Clean(entries);
            result.RemoveAll(e => e.Pid == Environment.ProcessId);
            result.Add(new Entry { Pid = Environment.ProcessId, Hue = hue });
            return result;
        });
    }

    /// <summary>
    /// 为当前进程分配一个与其它已注册窗口不同的色相。
    /// </summary>
    /// <returns>分配到的色相，取值 0~360</returns>
    public static int ClaimDistinctHue()
    {
        var result = _Palette[0];
        _Modify(entries =>
        {
            var cleaned = _Clean(entries);
            cleaned.RemoveAll(e => e.Pid == Environment.ProcessId);
            var used = cleaned.Select(e => e.Hue).ToHashSet();
            // 优先从备选面板中挑选未使用的色相
            var hue = _Palette.FirstOrDefault(h => !used.Contains(h), -1);
            if (hue < 0)
            {
                // 面板用尽：在色相环上寻找第一个空闲值
                hue = Enumerable.Range(0, 360).FirstOrDefault(h => !used.Contains(h), _Palette[0]);
            }
            cleaned.Add(new Entry { Pid = Environment.ProcessId, Hue = hue });
            result = hue;
            return cleaned;
        });
        return result;
    }

    /// <summary>
    /// 移除当前进程的注册项。
    /// </summary>
    public static void Unregister()
    {
        _Modify(entries =>
        {
            var cleaned = _Clean(entries);
            cleaned.RemoveAll(e => e.Pid == Environment.ProcessId);
            return cleaned;
        });
    }

    /// <summary>
    /// 清理已退出进程的残留注册项。
    /// </summary>
    private static List<Entry> _Clean(List<Entry> entries)
    {
        entries.RemoveAll(e => !_IsProcessAlive(e.Pid));
        return entries;
    }

    private static bool _IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读取、修改并写回注册表，并发冲突时自动重试。
    /// </summary>
    private static void _Modify(Func<List<Entry>, List<Entry>> modify)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                List<Entry> entries;
                if (File.Exists(_FilePath))
                {
                    var json = File.ReadAllText(_FilePath);
                    entries = JsonSerializer.Deserialize<List<Entry>>(json, _JsonOptions) ?? [];
                }
                else
                {
                    entries = [];
                }
                entries = modify(entries);
                var directory = Path.GetDirectoryName(_FilePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(_FilePath, JsonSerializer.Serialize(entries, _JsonOptions));
                return;
            }
            catch (Exception)
            {
                // 并发写入冲突或文件损坏时重试，最终失败也不影响窗口正常启动
                if (attempt == 2) return;
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }
}
