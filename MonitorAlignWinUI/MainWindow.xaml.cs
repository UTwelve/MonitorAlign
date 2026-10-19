using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition.SystemBackdrops;   // MicaController（能力探测）
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;                     // MicaBackdrop / MicaKind
using MonitorAlign.Services;
using Windows.Graphics;

namespace MonitorAlign;

/// <summary>
/// 主窗口。材质（Mica）用系统 SystemBackdrop；标题栏内容延伸由
/// <see cref="Window.ExtendsContentIntoTitleBar"/> + ShellPage 的 48px 标题行完成
/// （winui3-skills app-shell 模式）。
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Title = Loc.WindowTitle;

        // Mica：Win11 才有；Win10 / 虚拟机上静默回落（skill 规则：先查 IsSupported）
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };

        ExtendsContentIntoTitleBar = true;

        // AppWindow 使用物理像素，XAML 使用有效像素；按 DPI 换算并限制到工作区。
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = Math.Max(1.0, Native.Win32Overlay.GetDpiForWindow(hwnd) / 96.0);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min((int)Math.Round(860 * scale), Math.Max(320, area.Width - 32));
        int height = Math.Min((int)Math.Round(780 * scale), Math.Max(320, area.Height - 32));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
                                              area.Y + (area.Height - height) / 2, width, height));
        AppWindow.SetIcon("Assets/app.ico");
        AppWindow.Title = Loc.WindowTitle;

        // 关窗拦截：WinUI 3 没有 OnClosing(CancelEventArgs)，取消要挂在 AppWindow 上
        AppWindow.Closing += OnAppWindowClosing;
    }

    /// <summary>
    /// 关窗 ≠ 退出：泛光还在跑时缩到托盘（用户明确要求"软件关了也有效"）。
    /// 真退出只有两条路 —— 托盘菜单「退出」，或先把泛光关掉再关窗。
    /// 演示模式不常驻，直接关。
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var glow = App.Services.GetRequiredService<IEdgeGlowService>();

        if (!App.AllowExit && !App.IsDemo && glow.Enabled)
        {
            args.Cancel = true;
            App.HideToTray();
            return;
        }

        Diag.Log("主窗口关闭：真退出");
        glow.Stop();
        App.Services.GetRequiredService<IAlignSession>().Suspend();
    }
}
