using System.Diagnostics;
using System.Windows;
using MonitorAlign.Themes;

namespace MonitorAlign.Pages;

/// <summary>
/// 设置页（导航底部固定项）。by / 版本号 / 明暗调节从主窗口页脚迁来这里（v1.6.0）。
/// 主题循环按钮的三态逻辑原样搬自 MainWindow，只是换了个住处。
/// </summary>
public partial class SettingsPage : System.Windows.Controls.Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Fill();
    }

    private void Fill()
    {
        TxtVersion.Text = Loc.AppVersion;
        ApplyThemeButton(ThemeManager.Current);
        ToggleAutoStart.IsChecked = App.GetAutoStart();

        // 窗口是 SizeToContent：高度对齐到对齐页（扣 Root 上下边距 40），切页不抖
        if (Window.GetWindow(this) is MainWindow mw)
            Root.MinHeight = Math.Max(mw.PageAlignContentHeight - 40, 0);
    }

    // ==================== 主题（三态循环） ====================

    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        var next = (ThemeManager.Mode)(((int)ThemeManager.Current + 1) % 3);
        ThemeManager.SetMode(next);
        ApplyThemeButton(next);

        // 拓扑图用我们自己的 Ma* 画刷、泛光缺省色取主题强调色——换肤后都要重取
        if (Window.GetWindow(this) is MainWindow mw)
            mw.OnThemeChanged();

        Diag.Log($"主题切换为 {next}（isLight={ThemeManager.EffectiveIsLight()}）");
    }

    private void ApplyThemeButton(ThemeManager.Mode mode)
    {
        BtnTheme.Icon = mode switch
        {
            ThemeManager.Mode.Light => new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.WeatherSunny20),
            ThemeManager.Mode.Dark => new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.WeatherMoon20),
            _ => new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.CircleHalfFill20),
        };
        string name = mode switch
        {
            ThemeManager.Mode.Light => Loc.ThemeLight,
            ThemeManager.Mode.Dark => Loc.ThemeDark,
            _ => Loc.ThemeAuto,
        };
        BtnTheme.ToolTip = Loc.Fmt(Loc.TooltipTheme, name);
    }

    // ==================== 开机自启 ====================

    private void ToggleAutoStart_Click(object sender, RoutedEventArgs e)
        => App.SetAutoStart(ToggleAutoStart.IsChecked == true);

    // ==================== 关于 ====================

    /// <summary>“by UTwelve” —— 用默认浏览器打开作者主页。</summary>
    private void BtnAuthor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Loc.AuthorUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Diag.Log("打开作者主页失败：" + ex.Message);
        }
    }
}
