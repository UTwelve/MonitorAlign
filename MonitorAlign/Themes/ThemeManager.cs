using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MonitorAlign.Themes;

/// <summary>
/// 主题管理：默认跟随系统（读 AppsUseLightTheme），可强制浅色/深色。
///
/// 换肤分两步，顺序不能反：
///   1. ApplicationThemeManager.Apply —— 让 Wpf.Ui 换掉整套 Fluent 2 令牌
///      （TextFillColor*、CardBackgroundFillColor*、ControlFillColor* …）。
///      所有以这些令牌取色的控件、以及 FluentWindow 的 Mica 明暗变体，
///      都由它一并处理，我们不用碰 DWM。
///   2. 替换我们自己的 Palette.* —— 只含 Wpf.Ui 没有的键（拓扑图、覆盖层标尺）。
/// </summary>
public static class ThemeManager
{
    public enum Mode { Auto, Light, Dark }

    public static Mode Current { get; private set; } = Mode.Auto;

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";

    /// <summary>系统当前是否为浅色主题。</summary>
    public static bool SystemIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue(AppsUseLightTheme) is int v) return v != 0;
        }
        catch { }
        return true;   // 读不到时按浅色处理
    }

    public static bool EffectiveIsLight() => Current switch
    {
        Mode.Light => true,
        Mode.Dark => false,
        _ => SystemIsLight()
    };

    /// <summary>设置模式并立即应用。</summary>
    public static void SetMode(Mode mode)
    {
        Current = mode;
        Apply();
    }

    /// <summary>系统主题变化时由窗口消息调用：仅在 Auto 模式下重新应用。</summary>
    public static void RefreshFromSystem()
    {
        if (Current == Mode.Auto) Apply();
    }

    public static void Apply()
    {
        var app = Application.Current;
        if (app == null) return;

        bool light = EffectiveIsLight();

        // 第一步：Wpf.Ui 的整套令牌 + 每个 FluentWindow 的 Mica 明暗
        ApplicationThemeManager.Apply(
            light ? ApplicationTheme.Light : ApplicationTheme.Dark,
            WindowBackdropType.Mica,
            updateAccent: true);

        // 第二步：我们自己的配色（Wpf.Ui 没有的那些键）
        var dicts = app.Resources.MergedDictionaries;

        for (int i = dicts.Count - 1; i >= 0; i--)
        {
            string src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("Palette.Light", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("Palette.Dark", StringComparison.OrdinalIgnoreCase))
                dicts.RemoveAt(i);
        }

        dicts.Insert(0, new ResourceDictionary
        {
            Source = new Uri(light ? "Themes/Palette.Light.xaml" : "Themes/Palette.Dark.xaml",
                             UriKind.Relative)
        });
    }
}
