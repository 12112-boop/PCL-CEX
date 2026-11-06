using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.Configuration.Storage;
using PCL.Core.App.Essentials;
using PCL.Core.App.IoC;

namespace PCL.Core.UI.Theme;

/// <summary>
/// 配色模式更改事件。
/// </summary>
/// <param name="isDarkMode">当前是否为深色模式</param>
/// <param name="theme">当前配色主题</param>
public delegate void ColorModeChangedEvent(bool isDarkMode, ColorTheme theme);

/// <summary>
/// 配色主题更改事件。
/// </summary>
/// <param name="theme">当前配色主题</param>
public delegate void ColorThemeChangedEvent(ColorTheme theme);

[LifecycleScope("theme", "主题", false)]
[LifecycleService(LifecycleState.WindowCreating)]
public sealed partial class ThemeService
{
    [AnyConfigItem<ToneProfileConfig>("UiToneProfiles", ConfigSource.Local)]
    public static partial ToneProfileConfig ToneProfiles { get; set; }

    [RegisterConfigEvent]
    public static ConfigEventRegistry OnColorModeConfigChanged => new(
        scope: Config.Preference.Theme.ColorModeConfig,
        trigger: ConfigEvent.Update,
        handler: _ => RefreshColorMode()
    );

    [RegisterConfigEvent]
    public static ConfigEventRegistry OnColorThemeConfigChanged => new(
        scope: [Config.Preference.Theme.DarkColorConfig, Config.Preference.Theme.LightColorConfig],
        trigger: ConfigEvent.Update,
        handler: e =>
        {
            // ignore no change or non-current color theme change
            if (e.OldValue == e.Value) return;
            if (IsDarkMode) { if (e.Item == Config.Preference.Theme.LightColorConfig) return; }
            else { if (e.Item == Config.Preference.Theme.DarkColorConfig) return; }
            // trigger color refresh
            if (Lifecycle.CurrentState > LifecycleState.Loading)
            {
                Lifecycle.CurrentApplication.Dispatcher.BeginInvoke(() =>
                {
                    ApplyColorResources();
                    ColorThemeChanged?.Invoke(CurrentTheme);
                    _AprilFoolLogic();
                });
            }
        }
    );

    [RegisterConfigEvent]
    public static ConfigEventRegistry OnCustomThemeConfigChanged => new(
        scope:
        [
            Config.Preference.Theme.CustomThemeEnabledConfig, Config.Preference.Theme.WindowHueConfig,
            Config.Preference.Theme.WindowSatConfig, Config.Preference.Theme.WindowLightConfig,
            Config.Preference.Theme.WindowDeltaConfig, Config.Preference.Theme.SecondWindowThemeConfig,
            Config.Preference.Theme.SecondWindowHueConfig
        ],
        trigger: ConfigEvent.Update,
        handler: e =>
        {
            if (e.OldValue == e.Value) return;
            if (Lifecycle.CurrentState > LifecycleState.Loading)
            {
                Lifecycle.CurrentApplication.Dispatcher.BeginInvoke(() =>
                {
                    ApplyColorResources();
                    ColorThemeChanged?.Invoke(CurrentTheme);
                });
            }
        }
    );

    [LifecycleStart]
    private static void _Start()
    {
        IsDarkMode = _IsDarkMode();
        _LogStatus();
        if (IsAutoColorWindow)
            // 额外启动的窗口：分配一个与其它窗口不同的色相
            _autoWindowHue = WindowColorRegistry.ClaimDistinctHue();
        else
            // 主窗口：登记自身色相，供其它窗口避让
            WindowColorRegistry.RegisterHue(GetCurrentThemeArgs().Hue);
        _RefreshAll();
        _StartConfigWatch();
    }

    [LifecycleStop]
    private static void _Stop()
    {
        _configWatchStopped = true;
        WindowColorRegistry.Unregister();
    }

    /// <summary>
    /// 需要在多个窗口间同步的主题配置键。
    /// </summary>
    private static readonly string[] _ThemeConfigKeys =
    [
        "UiDarkMode", "UiDarkColor", "UiLightColor", "UiCustomTheme",
        "UiLauncherHue", "UiLauncherSat", "UiLauncherLight", "UiLauncherDelta",
        "UiSecondWindowTheme", "UiSecondWindowHue"
    ];

    private static Thread? _configWatchThread;
    private static volatile bool _configWatchStopped;
    private static Dictionary<string, string> _themeConfigSnapshot = [];

    /// <summary>
    /// 自动分配到的窗口色相；未分配时为 -1。
    /// </summary>
    private static int _autoWindowHue = -1;

    /// <summary>
    /// 开窗动画期间临时使用的色相；动画未进行时为 <c>null</c>。
    /// </summary>
    private static int? _hueOverride;

    /// <summary>
    /// 启动主题配置监视。<br/>
    /// 多个窗口共用同一份本地配置文件，而配置事件仅在当前进程内生效；
    /// 因此在任意窗口更改主题后，其它窗口通过轮询配置文件在数秒内同步刷新。
    /// </summary>
    private static void _StartConfigWatch()
    {
        _themeConfigSnapshot = _ReadThemeConfig();
        _configWatchStopped = false;
        _configWatchThread = new Thread(_ConfigWatchLoop)
        {
            IsBackground = true,
            Name = "Theme Config Watch"
        };
        _configWatchThread.Start();
    }

    private static void _ConfigWatchLoop()
    {
        while (!_configWatchStopped)
        {
            Thread.Sleep(2000);
            if (_configWatchStopped) return;
            try
            {
                var current = _ReadThemeConfig();
                if (_SnapshotEquals(current)) continue;
                _themeConfigSnapshot = current;
                Context.Debug("检测到主题配置更改，正在同步刷新");
                var app = Lifecycle.CurrentApplication;
                if (app is null || app.Dispatcher.HasShutdownStarted) continue;
                app.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        RefreshColorMode();
                        ApplyGrayResources();
                        ApplyColorResources();
                        ColorThemeChanged?.Invoke(CurrentTheme);
                    }
                    catch (Exception ex)
                    {
                        Context.Warn("同步刷新主题失败", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                Context.Warn("监视主题配置失败", ex);
            }
        }
    }

    private static bool _SnapshotEquals(Dictionary<string, string> other)
    {
        if (_themeConfigSnapshot.Count != other.Count) return false;
        foreach (var (key, value) in _themeConfigSnapshot)
            if (!other.TryGetValue(key, out var otherValue) || otherValue != value) return false;
        return true;
    }

    /// <summary>
    /// 直接读取本地配置文件中的主题相关键值，用于跨窗口同步。
    /// </summary>
    private static Dictionary<string, string> _ReadThemeConfig()
    {
        var result = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(ConfigService.LocalConfigPath)) return result;
            var provider = new YamlFileProvider(ConfigService.LocalConfigPath);
            foreach (var key in _ThemeConfigKeys)
                if (provider.Exists(key))
                    result[key] = provider.Get<string>(key) ?? string.Empty;
        }
        catch (Exception ex)
        {
            Context.Warn("读取主题配置失败", ex);
        }
        return result;
    }

    private static void _LogStatus()
    {
        Context.Debug($"当前状态: {(IsDarkMode ? "Dark" : "Light")}, {CurrentTheme}");
    }

    private static bool _IsDarkMode() => Config.Preference.Theme.ColorMode switch
    {
        ColorMode.Light => false,
        ColorMode.Dark => true,
        ColorMode.System => SystemThemeHelper.IsSystemInDarkMode(),
        _ => false
    };

    /// <summary>
    /// 当前是否为深色主题。
    /// </summary>
    public static bool IsDarkMode { get; private set; }

    /// <summary>
    /// 配色模式更改事件。
    /// </summary>
    public static event ColorModeChangedEvent? ColorModeChanged;

    /// <summary>
    /// 配色主题更改事件。
    /// </summary>
    public static event ColorThemeChangedEvent? ColorThemeChanged;

    private static void _RefreshAll()
    {
        ApplyGrayResources();
        ApplyColorResources();
        ColorModeChanged?.Invoke(IsDarkMode, CurrentTheme);
        _AprilFoolLogic();
    }

    private static void _AprilFoolLogic()
    {
        // HMCL 配色不再受愚人节限制，任何时候都可以选择
        if (Basics.IsAprilFool) Config.Preference.WindowTitleTypeConfig.TriggerEvent(ConfigEvent.Changed, null);
    }

    /// <summary>
    /// 刷新配色模式，若检测到当前配色模式有实际更改，则会触发主题刷新。
    /// </summary>
    public static void RefreshColorMode()
    {
        var isDarkMode = _IsDarkMode();
        if (IsDarkMode == isDarkMode) return;
        Context.Info("正在更改配色模式");
        IsDarkMode = isDarkMode;
        _LogStatus();
        if (Lifecycle.CurrentState > LifecycleState.Loading)
        {
            Lifecycle.CurrentApplication.Dispatcher.BeginInvoke(_RefreshAll);
        }
    }

    /// <summary>
    /// 当前使用的色彩属性。
    /// </summary>
    public static ToneProfile CurrentTone => IsDarkMode ? ToneProfiles.Dark : ToneProfiles.Light;

    /// <summary>
    /// 当前使用的主题色。
    /// </summary>
    public static ColorTheme CurrentTheme
    {
        get
        {
            var theme = Config.Preference.Theme;
            return IsDarkMode ? theme.DarkColor : theme.LightColor;
        }
        set
        {
            var theme = Config.Preference.Theme;
            var config = IsDarkMode ? theme.DarkColorConfig : theme.LightColorConfig;
            config.SetValue(value);
        }
    }

    /// <summary>
    /// 当前进程是否为额外启动的第二个（及以后的）窗口。
    /// </summary>
    public static bool IsSecondaryWindow => Basics.IsSecondaryWindow;

    /// <summary>
    /// 当前进程是否为需要自动配色的窗口（通过 --new-window 参数或多开启动的额外窗口）。
    /// </summary>
    public static bool IsAutoColorWindow => Basics.IsSecondaryWindow || SingleInstanceService.IsSecondaryInstance;

    /// <summary>
    /// 当前进程自动分配到的窗口色相；未分配时为 -1。
    /// </summary>
    public static int AutoWindowHue => _autoWindowHue;

    /// <summary>
    /// 新窗口开启动画结束后应落入的色相：手动指定了第二窗口配色时使用手动色相，否则使用自动分配的色相。
    /// </summary>
    public static int WindowSettleHue
    {
        get
        {
            if (Config.Preference.Theme.SecondWindowTheme >= 0)
                return _GetSecondaryWindowThemeArgs().Hue;
            return _autoWindowHue >= 0 ? _autoWindowHue : 300;
        }
    }

    /// <summary>
    /// 背景色相渐变角度。
    /// </summary>
    public static int HueDelta => Math.Clamp(Config.Preference.Theme.WindowDelta, 0, 90);

    /// <summary>
    /// 获取预设色彩主题对应的各种参数。
    /// </summary>
    private static (int Hue, double LightAdjust, double ChromaAdjust) _GetPresetThemeArgs(ColorTheme theme) => theme switch
    {
        ColorTheme.SkyBlue => (235, 0.36, 0.2),
        ColorTheme.CatBlue => (255, 0, -0.2),
        ColorTheme.DeathBlue => (268, -0.05, -0.1),
        ColorTheme.HmclBlue => (275, -0.03, -0.35),
        ColorTheme.Custom => _GetCustomThemeArgs(Config.Preference.Theme.WindowHue,
            Config.Preference.Theme.WindowLight, Config.Preference.Theme.WindowSat),
        _ => (255, 0, -0.2)
    };

    /// <summary>
    /// 依据色相、明度、饱和度（均为 0~100 的整数，50 为原始值）计算主题参数。
    /// </summary>
    private static (int Hue, double LightAdjust, double ChromaAdjust) _GetCustomThemeArgs(int hue, int light, int sat) =>
        (Math.Clamp(hue, 0, 360),
            (Math.Clamp(light, 0, 100) - 50) / 50d,
            (Math.Clamp(sat, 0, 100) - 50) / 50d);

    /// <summary>
    /// 获取第二个窗口使用的主题参数。
    /// </summary>
    private static (int Hue, double LightAdjust, double ChromaAdjust) _GetSecondaryWindowThemeArgs()
    {
        var theme = Config.Preference.Theme;
        // 自定义色相
        if (theme.SecondWindowTheme == (int)ColorTheme.Custom)
            return _GetCustomThemeArgs(theme.SecondWindowHue, theme.WindowLight, theme.WindowSat);
        // 跟随主窗口
        if (theme.SecondWindowTheme < 0)
            return theme.CustomThemeEnabled
                ? _GetCustomThemeArgs(theme.WindowHue, theme.WindowLight, theme.WindowSat)
                : _GetPresetThemeArgs(CurrentTheme);
        // 指定预设配色
        return _GetPresetThemeArgs((ColorTheme)theme.SecondWindowTheme);
    }

    /// <summary>
    /// 获取当前色彩主题对应的各种参数。
    /// </summary>
    public static (int Hue, double LightAdjust, double ChromaAdjust) GetCurrentThemeArgs()
    {
        if (IsAutoColorWindow) return _GetAutoWindowThemeArgs();
        var theme = Config.Preference.Theme;
        if (theme.CustomThemeEnabled)
            return _GetCustomThemeArgs(theme.WindowHue, theme.WindowLight, theme.WindowSat);
        return _GetPresetThemeArgs(CurrentTheme);
    }

    /// <summary>
    /// 获取自动配色窗口的主题参数：未手动指定第二窗口配色时使用自动分配的色相，
    /// 开窗动画进行中则使用动画的临时色相。
    /// </summary>
    private static (int Hue, double LightAdjust, double ChromaAdjust) _GetAutoWindowThemeArgs()
    {
        var theme = Config.Preference.Theme;
        var args = theme.SecondWindowTheme >= 0
            ? _GetSecondaryWindowThemeArgs()
            : _GetCustomThemeArgs(_autoWindowHue >= 0 ? _autoWindowHue : 300, theme.WindowLight, theme.WindowSat);
        return _hueOverride is { } overrideHue ? (overrideHue, args.LightAdjust, args.ChromaAdjust) : args;
    }

    /// <summary>
    /// 播放新窗口的开场配色动画：先在约 2 秒内快速循环彩色，再平滑过渡到目标色相。<br/>
    /// 仅对自动配色的额外窗口调用。
    /// </summary>
    /// <param name="targetHue">动画结束后落入的色相</param>
    public static void PlayWindowColorAnimation(int targetHue)
    {
        if (targetHue < 0 || targetHue > 360) return;
        Context.Debug($"开始播放开窗配色动画，目标色相 {targetHue}");
        var thread = new Thread(() => _ColorAnimationLoop(targetHue))
        {
            IsBackground = true,
            Name = "Window Color Animation"
        };
        thread.Start();
    }

    private static void _ColorAnimationLoop(int targetHue)
    {
        var app = Lifecycle.CurrentApplication;
        if (app is null) return;
        var dispatcher = app.Dispatcher;

        // 第一阶段：彩色循环，持续约 2 秒
        const int flashDuration = 2000;
        const int flashInterval = 60;
        const int hueStep = 36;
        var hue = targetHue;
        for (var elapsed = 0; elapsed < flashDuration; elapsed += flashInterval)
        {
            if (dispatcher.HasShutdownStarted) return;
            _hueOverride = hue;
            _ApplyColorOnDispatcher(dispatcher);
            Thread.Sleep(flashInterval);
            hue = (hue + hueStep) % 360;
        }

        // 第二阶段：平滑过渡到目标色相，持续 400 毫秒
        const int fadeDuration = 400;
        const int fadeInterval = 20;
        var startHue = hue;
        for (var elapsed = 0; elapsed < fadeDuration; elapsed += fadeInterval)
        {
            if (dispatcher.HasShutdownStarted) return;
            var progress = (double)elapsed / fadeDuration;
            var delta = ((targetHue - startHue + 540) % 360) - 180;
            _hueOverride = (int)Math.Round((startHue + delta * progress + 360) % 360);
            _ApplyColorOnDispatcher(dispatcher);
            Thread.Sleep(fadeInterval);
        }

        // 交还给自动分配的色相
        _hueOverride = null;
        if (!dispatcher.HasShutdownStarted) _ApplyColorOnDispatcher(dispatcher);
        Context.Debug($"开窗配色动画结束，已落入色相 {targetHue}");
    }

    private static void _ApplyColorOnDispatcher(Dispatcher dispatcher)
    {
        dispatcher.BeginInvoke(() =>
        {
            try
            {
                ApplyColorResources();
            }
            catch (Exception ex)
            {
                Context.Warn("应用窗口配色失败", ex);
            }
        });
    }

    private static double _AdjustLinear(double value, double adjustment)
    {
        if (adjustment == 0) return value;
        // 确保输入在合理范围内
        value = Math.Clamp(value, 0.0, 1.0);
        adjustment = Math.Clamp(adjustment, -1.0, 1.0);
        // 非对称线性插值
        return adjustment switch
        {
            > 0 => value + (1.0 - value) * adjustment,
            _ => value + value * adjustment
        };
    }

    private static CatColorResource[] _CalculateGrays(ToneProfile tone) => [
        LabColor.FromLch(tone.L1).ToCatColor("Gray1"),
        LabColor.FromLch(tone.L2).ToCatColor("Gray2"),
        LabColor.FromLch(tone.L3).ToCatColor("Gray3"),
        LabColor.FromLch(tone.L4).ToCatColor("Gray4"),
        LabColor.FromLch(tone.L5).ToCatColor("Gray5"),
        LabColor.FromLch(tone.L6).ToCatColor("Gray6"),
        LabColor.FromLch(tone.L7).ToCatColor("Gray7"),
        LabColor.FromLch(tone.L8).ToCatColor("Gray8"),
        LabColor.FromLch(tone.LWhite, alpha:tone.AHalfWhite).ToCatColor("HalfWhite", false),
        LabColor.FromLch(tone.LWhite, alpha:tone.ASemiWhite).ToCatColor("SemiWhite", false),
        LabColor.FromLch(tone.LWhite).ToCatColor("White", false),
        LabColor.FromLch(tone.LWhite, alpha:tone.ATransparent).ToCatColor("Transparent", false),
        LabColor.FromLch(tone.LBackground, alpha:tone.ABackground).ToCatColor("TransparentBackground", false),
        LabColor.FromLch(tone.LBackground).ToCatColor("Background", false),
        LabColor.FromLch(tone.LBackground, alpha:tone.AToolTip).ToCatColor("ToolTip", false),
        LabColor.FromLch(tone.L7, 0.25, 30, tone.AHalfTransparent).ToCatColor("RedBack", false),
        LabColor.FromLch(tone.LForeground).ToCatColor("Memory", false),
    ];

    private static CatColorResource[] _CalculateColors(ToneProfile tone, (int hue, double lightAdj, double chromaAdj) args) => [
        LabColor.FromLch(_AdjustLinear(tone.L1, args.lightAdj * 0.1), _AdjustLinear(tone.C1, args.chromaAdj * 0.25), args.hue).ToCatColor("1"),
        LabColor.FromLch(_AdjustLinear(tone.L2, args.lightAdj), _AdjustLinear(tone.C2, args.chromaAdj), args.hue).ToCatColor("2"),
        LabColor.FromLch(_AdjustLinear(tone.L3, args.lightAdj), _AdjustLinear(tone.C3, args.chromaAdj), args.hue).ToCatColor("3"),
        LabColor.FromLch(_AdjustLinear(tone.L4, args.lightAdj), _AdjustLinear(tone.C4, args.chromaAdj), args.hue).ToCatColor("4"),
        LabColor.FromLch(_AdjustLinear(tone.L5, args.lightAdj), _AdjustLinear(tone.C5, args.chromaAdj), args.hue).ToCatColor("5"),
        LabColor.FromLch(_AdjustLinear(tone.L6, args.lightAdj), _AdjustLinear(tone.C6, args.chromaAdj), args.hue).ToCatColor("6"),
        LabColor.FromLch(_AdjustLinear(tone.L7, args.lightAdj), _AdjustLinear(tone.C7, args.chromaAdj), args.hue).ToCatColor("7"),
        LabColor.FromLch(_AdjustLinear(tone.L8, args.lightAdj), _AdjustLinear(tone.C8, args.chromaAdj), args.hue).ToCatColor("8"),
        LabColor.FromLch(_AdjustLinear(tone.L8, args.lightAdj), _AdjustLinear(tone.C8, args.chromaAdj), args.hue, tone.ASemiTransparent).ToCatColor("SemiTransparent", false),
        LabColor.FromLch(_AdjustLinear(tone.L5, args.lightAdj), _AdjustLinear(tone.C5, args.chromaAdj), args.hue).ToCatColor("Bg0"),
        LabColor.FromLch(_AdjustLinear(tone.L7, args.lightAdj), _AdjustLinear(tone.C7, args.chromaAdj), args.hue, tone.ASemiWhite).ToCatColor("Bg1"),
    ];

    private static CatColorResource[] LightGrayCache { get => field ??= _CalculateGrays(ToneProfiles.Light); set; } = null!;

    private static CatColorResource[] DarkGrayCache { get => field ??= _CalculateGrays(ToneProfiles.Dark); set; } = null!;

    /// <summary>
    /// 清除灰度配色的计算缓存。
    /// </summary>
    public static void InvalidateGrayCache()
    {
        LightGrayCache = null!;
        DarkGrayCache = null!;
    }

    /// <summary>
    /// 应用灰度配色到 WPF 资源字典。
    /// </summary>
    public static void ApplyGrayResources()
    {
        var cache = IsDarkMode ? DarkGrayCache : LightGrayCache;
        foreach (var c in cache) c.Apply();
    }

    /// <summary>
    /// 应用彩色配色到 WPF 资源字典。
    /// </summary>
    public static void ApplyColorResources()
    {
        var args = GetCurrentThemeArgs();
        var colors = _CalculateColors(CurrentTone, args);
        foreach (var c in colors) c.Apply();
        // 主窗口主题变化后更新注册的色相，便于其它窗口避让
        if (!IsAutoColorWindow) WindowColorRegistry.RegisterHue(args.Hue);
    }
}
