using System.IO;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using MonitorAlign.Core;
using MonitorAlign.Overlay;
using MonitorAlign.Services;
using MonitorAlign.ViewModels;

namespace MonitorAlign;

/// <summary>
/// WinUI 3 版入口。与 WPF 版同宗：命令行参数、单实例、语言选择、自检模式、
/// 诊断日志的语义完全一致；UI 层按 Fluent 2 全部重写（winui3-skills 模式）。
/// </summary>
public partial class App : Application
{
    /// <summary>组合根：全部服务与 ViewModel 的注册处（winui3-skills MVVM+DI 模式）。</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>主窗口（App 级引用：托盘/单实例唤醒都要摸它）。</summary>
    public static Window MainWindow { get; private set; } = null!;

    /// <summary>演示模式（--demo / --arrange）：不读写真实硬件、不碰存档。</summary>
    public static bool IsDemo { get; private set; }

    /// <summary>--arrange left|above|right|below：模拟布局塞进真实桌面最大屏（截图验证）。</summary>
    public static string Arrange { get; private set; } = "";

    /// <summary>--lang en|zh：强制界面语言。</summary>
    public static string Lang { get; private set; } = "";

    /// <summary>--diag / --navtest：诊断日志开关。</summary>
    public static bool DiagEnabled { get; private set; }
    public static bool NavTest { get; private set; }

    /// <summary>--page align|glow|dev|settings：启动落到指定导航页（截图验收用）。</summary>
    public static string InitialPage { get; private set; } = "";

    /// <summary>--glow：启动即开启边缘泛光（演示模式也能用，方便试效果与截图）。</summary>
    public static bool GlowAtStart { get; private set; }

    /// <summary>--glowtest 0..1：强制所有接缝固定强度常亮（截图验收）。-1 = 关闭。</summary>
    public static double GlowTestIntensity { get; private set; } = -1;

    /// <summary>--gauge d1,d2：启动即摆标尺并注入位移（无鼠标验收通路）。null = 不用。</summary>
    public static (double First, double Second)? GaugeTest { get; private set; }

    /// <summary>--background：开机自启的后台形态——主窗不出现，只留托盘 + 恢复泛光。</summary>
    public static bool BackgroundStart { get; private set; }

    /// <summary>托盘「退出」时置真，绕过关窗时「缩到托盘」的拦截。</summary>
    public static bool AllowExit { get; set; }

    /// <summary>主窗口此刻是否可见（后台形态起始为 false）。</summary>
    public static bool MainWindowVisible { get; private set; } = true;

    /// <summary>主窗口显隐变化（对齐页靠它决定标尺起不起）。</summary>
    public static event Action<bool> MainWindowVisibilityChanged;

    private static ITrayService _tray;

    /// <summary>
    /// <c>--overlaytest</c>：只起覆盖层（标尺），不起主窗口。
    /// 配合 <c>--arrange</c> 造出可截图的布局，并把窗口矩形与命中矩形写成探测文件，
    /// 供脚本用 WindowFromPoint 验证"按钮吃鼠标、空白处穿透"。
    /// </summary>
    public static bool OverlayTest { get; private set; }

    /// <summary>
    /// <c>--punch region|none</c>：<c>none</c> 是不打洞的**负控** ——
    /// 整窗都吃鼠标，于是探测脚本必须给出与 region 相反的结果；
    /// 若两者结果一样，说明探测方法本身没在判别，而不是实现有问题。
    /// </summary>
    public static string Punch { get; private set; } = "region";

    /// <summary>覆盖层存活引用（不挂静态就会被 GC 收走，窗口跟着消失）。</summary>
    private static readonly List<OverlayWindowBase> Overlays = new();

    public static string OverlayProbePath =>
        Path.Combine(Path.GetTempPath(), "MonitorAlign-overlay.txt");

    /// <summary>登记覆盖层存活引用。不挂静态就会被 GC 收走，窗口跟着消失。</summary>
    public static void TrackOverlay(OverlayWindowBase w)
    {
        lock (Overlays) { if (!Overlays.Contains(w)) Overlays.Add(w); }
    }

    /// <summary>释放覆盖层引用（会话结束 / 换接缝时旧层要丢）。</summary>
    public static void ForgetOverlay(OverlayWindowBase w)
    {
        lock (Overlays) { Overlays.Remove(w); }
    }

    /// <summary>测试/演示用的显示列表：--arrange 优先，否则用固定的 8 屏压力布局。</summary>
    public static List<DisplayInfo> DemoDisplays() =>
        Arrange.Length > 0 ? DisplayEnumerator.ArrangeDemo(Arrange) : DisplayEnumerator.Demo();

    // ==================== 主窗口显隐（托盘常驻）====================

    /// <summary>把主窗口亮出来（托盘双击 / 菜单「打开」/ 第二个实例来敲门）。</summary>
    public static void ShowMainWindow()
    {
        var w = MainWindow;
        if (w == null) return;

        w.AppWindow.Show();
        w.Activate();

        if (MainWindowVisible) return;
        MainWindowVisible = true;
        if (Views.ShellPage.CurrentTag == "align")
            Services.GetRequiredService<IAlignSession>().Resume();
        MainWindowVisibilityChanged?.Invoke(true);
        Diag.Log("主窗口已显示");
    }

    /// <summary>
    /// 收起主窗口（关窗但泛光还在跑时走这里）。标尺跟着收 ——
    /// 窗口都看不见了，屏幕上还杵着两条标尺就是 bug。
    /// </summary>
    public static void HideMainWindow()
    {
        var w = MainWindow;
        if (w == null || !MainWindowVisible) return;

        MainWindowVisible = false;
        Services.GetRequiredService<SeamScaleViewModel>().Deactivate();
        Services.GetRequiredService<IAlignSession>().Suspend();
        MainWindowVisibilityChanged?.Invoke(false);
        w.AppWindow.Hide();
        Diag.Log("主窗口已隐藏（托盘常驻）");
    }

    /// <summary>建托盘图标（幂等）。</summary>
    public static void EnsureTray()
    {
        if (_tray != null) return;

        _tray = Services.GetRequiredService<ITrayService>();
        _tray.OpenRequested += ShowMainWindow;
        _tray.ExitRequested += ExitForReal;
        _tray.Show();
    }

    /// <summary>关窗但收进托盘：藏窗口 + 建托盘 + 弹一次气泡。</summary>
    public static void HideToTray()
    {
        HideMainWindow();
        EnsureTray();
        _tray?.ShowBalloonOnce();
        Diag.Log("泛光运行中：主窗口缩到托盘");
    }

    /// <summary>真退出：停泛光、收托盘、放行关窗。</summary>
    public static void ExitForReal()
    {
        Diag.Log("退出：托盘/关窗触发");
        AllowExit = true;
        Services.GetRequiredService<IEdgeGlowService>().Stop();
        Services.GetRequiredService<IAlignSession>().Suspend();
        _tray?.Dispose();
        _tray = null;
        MainWindow?.Close();
    }

    public App()
    {
        InitializeComponent();

        var services = new ServiceCollection();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IAlignSession, AlignSession>();
        services.AddSingleton<IEdgeGlowService, EdgeGlowService>();
        services.AddSingleton<ISeamGaugeService, SeamGaugeService>();
        services.AddSingleton<AlignViewModel>();
        services.AddSingleton<GlowViewModel>();
        services.AddSingleton<SeamScaleViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ITrayService, TrayService>();
        Services = services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] argv = Environment.GetCommandLineArgs();
        string Value(string name)
        {
            for (int i = 0; i < argv.Length - 1; i++)
                if (string.Equals(argv[i], name, StringComparison.OrdinalIgnoreCase)) return argv[i + 1];
            return "";
        }
        bool Has(string name) => argv.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        Arrange = Value("--arrange");
        IsDemo = Has("--demo") || Arrange.Length > 0;
        Lang = Value("--lang");
        InitialPage = Value("--page");
        NavTest = Has("--navtest");
        GlowAtStart = Has("--glow");
        BackgroundStart = Has("--background");
        bool infoMode = Has("--info");
        bool geomCheck = Has("--geomcheck");
        OverlayTest = Has("--overlaytest");
        if (int.TryParse(Value("--fill"), out int fillAlpha)) OverlayWindowBase.FillAlpha = fillAlpha;
        OverlayWindowBase.LayeredEnabled = !string.Equals(Value("--layered"), "off",
                                                          StringComparison.OrdinalIgnoreCase);
        if (Value("--punch").Length > 0) Punch = Value("--punch");
        // 玻璃步骤的 A/B 开关（见 OverlayWindowBase.GlassEnabled 的注释）
        OverlayWindowBase.GlassEnabled = !string.Equals(Value("--glass"), "off",
                                                        StringComparison.OrdinalIgnoreCase);
        // 可激活性的 A/B 开关（见 OverlayWindowBase.ForceNoActivate 的注释）
        OverlayWindowBase.ForceNoActivate = string.Equals(Value("--act"), "off",
                                                          StringComparison.OrdinalIgnoreCase);
        if (double.TryParse(Value("--glowtest"), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double gti))
            GlowTestIntensity = Math.Clamp(gti, 0, 1);

        // --gauge d1,d2：启动即摆标尺并注入位移（锁屏/无人值守时的无鼠标验收通路，
        // 见 SeamScaleViewModel.DebugPlace 的注释）。
        string gaugeArg = Value("--gauge");
        if (gaugeArg.Length > 0)
        {
            var parts = gaugeArg.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2
                && double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out double q1)
                && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out double q2))
            {
                GaugeTest = (q1, q2);
            }
        }

        DiagEnabled = Has("--diag") || NavTest || GaugeTest != null;

        // 语言必须先于任何 XAML 页面创建：页面里的 {x:Bind loc:Loc.Xxx} 在构造时取值
        Loc.Init(Lang);
        Diag.Enabled = DiagEnabled;
        Diag.Reset();
        Diag.Log($"启动（WinUI3）：demo={IsDemo} arrange={Arrange} background={BackgroundStart} " +
                 $"info={infoMode} geom={geomCheck} lang={Loc.Lang} cwd={AppContext.BaseDirectory}");

        // 未处理异常落盘：看不到界面的环境下这是唯一的线索。
        // WinUI 3 没有 MessageBox 可用，只记日志。
        UnhandledException += (_, e) =>
        {
            Diag.Log("未处理异常：" + e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Diag.Log("域异常：" + e.ExceptionObject);

        // ---- 无界面模式：先跑完就退，绝不建窗口 ----
        if (geomCheck)
        {
            Diag.Log("进入几何自检模式");
            Environment.Exit(SelfCheck.Run() == 0 ? 0 : 2);
            return;
        }

        if (infoMode)
        {
            RunInfoMode();
            Environment.Exit(0);
            return;
        }

        if (OverlayTest)
        {
            RunOverlayTest();
            return;
        }

        // 单实例：已经有一份在跑就把它叫出来，自己不再开一份。
        // --info / --geomcheck / --overlaytest 都在上面已经返回，不受影响。
        if (!SingleInstance.Claim())
        {
            Diag.Log("已有实例在运行：把它的窗口提到前台后退出");
            SingleInstance.SignalExisting();
            Environment.Exit(0);
            return;
        }
        SingleInstance.StartListening(ShowMainWindow);

        // 后台形态：窗口照样要建（XAML 岛、Loaded、存档恢复都挂在这一步），
        // 只是建完立刻收走 —— 先置标志，AlignPage 的 Loaded 就不会把标尺弹出来。
        MainWindowVisible = !BackgroundStart;

        MainWindow = new MainWindow();
        MainWindow.Activate();
        if (BackgroundStart) MainWindow.AppWindow.Hide();
        Diag.Log($"MainWindow 已创建并激活（可见={MainWindowVisible}）");

        // 泛光要按存档（或 --glow）恢复，而且它有窗口、必须在 UI 线程起 ——
        // 所以放在主窗口建好之后，而不是更早的无界面分支里。
        Services.GetRequiredService<GlowViewModel>().RestoreFromSettings();
        if (MainWindow.Content is FrameworkElement themeRoot)
            themeRoot.ActualThemeChanged += (_, _) =>
                Services.GetRequiredService<IEdgeGlowService>().RefreshColors();

        if (BackgroundStart)
        {
            EnsureTray();
            Diag.Log("后台形态：托盘就绪，主窗口保持隐藏");
        }

        // --gauge d1,d2：等主窗口 Loaded、导航页建好之后再摆标尺 ——
        // DebugPlace 要走 VM 的 Activate()，那时接缝清单才刚从拓扑里填出来。
        if (GaugeTest != null)
        {
            var (d1, d2) = GaugeTest.Value;
            var q = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (q != null)
                q.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                             () => Services.GetRequiredService<SeamScaleViewModel>().DebugPlace(d1, d2));
        }
    }

    /// <summary>
    /// 覆盖层验收（<c>--overlaytest</c>）：起标尺层并把窗口/命中矩形写进探测文件。
    /// 不建主窗口 —— 这样脚本量到的每个 hwnd 归属都没有歧义。
    /// </summary>
    private static void RunOverlayTest()
    {
        string dir = Arrange.Length > 0 ? Arrange : "right";
        var displays = DisplayEnumerator.ArrangeDemo(dir);
        var topo = TopologyAnalyzer.Analyze(displays);

        var sb = new StringBuilder();
        // 这一行留给脚本做"探测器确实来自 WinUI3 版"的判别，
        // 产物名已与 WPF 版统一成 MonitorAlign.exe，所以标记单独写在前面。
        sb.AppendLine($"exe=MonitorAlign winui3=1 dir={dir} punch={Punch}");

        if (topo.Seams.Count == 0)
        {
            sb.AppendLine("致命：演示布局里没有接缝");
            Diag.Log("overlaytest：没有接缝");
        }
        else
        {
            var seam = topo.Seams[0];
            double hostScale = displays[0].Scale <= 0 ? 1 : displays[0].Scale;
            sb.AppendLine($"seam={(seam.Vertical ? "vertical" : "horizontal")} @{seam.SeamCoord} " +
                          $"base=#{seam.Base.Index} target=#{seam.Target.Index} " +
                          $"anchor={seam.MidPoint} bounds=[{seam.OverlapStart},{seam.OverlapEnd}] " +
                          $"scale={hostScale:0.##}");

            var rulers = new List<RulerOverlay>();
            foreach (var (disp, isBase) in new[] { (seam.Base, true), (seam.Target, false) })
            {
                string tag = isBase ? "base" : "target";
                var overlay = new RulerOverlay(disp, seam, isBase);

                // 交互事件全部打进诊断日志：验收脚本据此判断"拖动/按钮/键盘到底有没有反应"。
                // overlaytest 里**不接**系统写入（没有订阅者去应用偏移），所以这些日志
                // 只证明事件流通了，不会改动真实显示排列。
                overlay.MarkerChanged += v => Diag.Log($"[{tag}] 标尺移动 → {v:0.##}");
                overlay.CommitRequested += v => Diag.Log($"[{tag}] 请求提交 → {v:0.##}（增量 {overlay.PendingDelta}）");
                overlay.AnchorSettled += v => Diag.Log($"[{tag}] 锚点落定 → {v:0.##}");
                overlay.ResetRequested += () => Diag.Log($"[{tag}] 请求回中");
                overlay.CloseRequested += () => Diag.Log($"[{tag}] 请求退出对齐");

                overlay.Present();
                overlay.SetPunchEnabled(Punch != "none");
                Overlays.Add(overlay);
                rulers.Add(overlay);

                sb.Append(overlay.Probe(tag));
            }

            // 逐层重建置顶序。
            // 创建两个窗口之间隔着几十毫秒，用户此时点开任何一个窗口（哪怕它本来在最底层），
            // 它都会插到**已创建的**那层之上 —— 实测 base 层就因此被一个随手的系统设置窗盖住，
            // 探测报"归了别人"。这不是打洞的问题（负控仍然是反的），纯粹是 z 序被搅了。
            // 全部建完再按序顶一遍，让置顶带里的相对次序确定下来。
            foreach (var r in rulers) r.BringToTop();
        }

        // 泛光层（--glow / --glowtest）：整窗穿透，探测点在它上面也必须落到别的窗口。
        // 与标尺层同屏跑，正好把两种命中策略放在一次验收里对照。
        if (GlowAtStart || GlowTestIntensity >= 0)
        {
            double level = GlowTestIntensity >= 0 ? GlowTestIntensity : 1.0;
            foreach (var seam in topo.Seams)
            {
                var glow = new GlowOverlay(seam, Windows.UI.Color.FromArgb(255, 0, 120, 212), 96,
                                           seam.Base.Scale);
                glow.SetBrightness(1.0);
                glow.SetIntensity(level);
                glow.BringToTop();          // 顶到标尺层之上，见下面 Probe 的判据
                Overlays.Add(glow);
                sb.Append(glow.Probe(seam.Label));
            }
        }

        try { File.WriteAllText(OverlayProbePath, sb.ToString()); } catch { }
        Diag.Log($"overlaytest：探测文件已写 {OverlayProbePath}");
        Diag.Log(sb.ToString());
    }

    /// <summary>
    /// 自检模式（--info）：把探测结果与拓扑分析写到 exe 同目录 / 临时目录，
    /// 便于在没有多屏环境的机器上验证算法（与 WPF 版逐字节同源）。
    /// </summary>
    private void RunInfoMode()
    {
        var displays = IsDemo ? DemoDisplays() : DisplayEnumerator.Enumerate();
        var topo = TopologyAnalyzer.Analyze(displays);

        var sb = new StringBuilder();
        sb.AppendLine("=== MonitorAlign 自检 ===");
        sb.AppendLine($"模式：{(IsDemo ? (Arrange.Length > 0 ? $"演示数据（--arrange {Arrange}）" : "演示数据（8 屏）") : "真实硬件")}");
        sb.AppendLine($"显示器数量：{displays.Count}");
        sb.AppendLine();

        sb.AppendLine("--- 显示器 ---");
        foreach (var d in displays)
        {
            sb.AppendLine($"#{d.Index} {d.DeviceName}");
            sb.AppendLine($"    友好名   : {d.FriendlyName}");
            sb.AppendLine($"    逻辑矩形 : ({d.Left},{d.Top})-({d.Right},{d.Bottom})  {d.Width}x{d.Height}");
            sb.AppendLine($"    DEVMODE  : dmPosition=({d.PositionX},{d.PositionY}) pels={d.PelsWidth}x{d.PelsHeight}");
            sb.AppendLine($"    缩放     : {d.Scale:0.##}  (DPI {d.DpiX})");
            sb.AppendLine($"    主屏     : {(d.IsPrimary ? "是" : "否")}");
            sb.AppendLine($"    物理尺寸 : {d.PhysicalWidthMm}x{d.PhysicalHeightMm} mm  ({d.MmPerPixelX:0.###} mm/px)");
        }
        sb.AppendLine();

        sb.AppendLine("--- 拓扑分析 ---");
        sb.AppendLine($"主屏：#{topo.Primary?.Index} {topo.Primary?.DeviceName}");
        sb.AppendLine($"接缝数：{topo.Seams.Count}（竖直 {topo.VerticalSeamCount} / 水平 {topo.HorizontalSeamCount}）");
        sb.AppendLine($"最大链深：{topo.MaxDepth}");
        sb.AppendLine();

        foreach (var s in topo.Seams)
        {
            if (s.Unreachable)
            {
                sb.AppendLine($"  [孤屏] #{s.Target.Index} 与主屏不连通，无法通过 BFS 对齐链处理");
                continue;
            }
            sb.AppendLine($"  {s.Label}  {s.DirectionText}接缝  深度{s.Depth}");
            sb.AppendLine($"      接缝坐标 : {(s.Vertical ? "x" : "y")}={s.SeamCoord}");
            sb.AppendLine($"      有效区间 : [{s.OverlapStart}, {s.OverlapEnd}]  长度 {s.OverlapLength}");
            sb.AppendLine($"      参考物点 : {s.MidPoint}");
            sb.AppendLine($"      间隙     : {s.Gap} (负=重叠)");
            sb.AppendLine($"      基准 → 待调: #{s.Base.Index} → #{s.Target.Index}");
        }

        string outText = sb.ToString();

        // 写到多个位置，保证任何运行方式下都能拿到结果
        foreach (string dir in new[]
                 {
                     Path.GetDirectoryName(Environment.ProcessPath),
                     AppContext.BaseDirectory,
                     Path.GetTempPath()
                 })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try { File.WriteAllText(Path.Combine(dir, "MonitorAlign-info.txt"), outText); }
            catch { }
        }

        try { Console.Out.Write(outText); } catch { }
    }
}
