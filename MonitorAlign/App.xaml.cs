using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using MonitorAlign.Core;
using MonitorAlign.Native;
using MonitorAlign.Themes;
using Microsoft.Win32;

namespace MonitorAlign;

/// <summary>诊断日志：仅在 --diag 参数下写入临时目录，用于无人值守环境验证 UI 初始化。</summary>
public static class Diag
{
    public static bool Enabled;
    public static string LogPath =>
        Path.Combine(Path.GetTempPath(), "MonitorAlign-diag.txt");

    public static void Log(string message)
    {
        if (!Enabled) return;
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}\r\n"); }
        catch { }
    }

    public static void Reset()
    {
        if (!Enabled) return;
        try { File.WriteAllText(LogPath, ""); } catch { }
    }
}

public partial class App : Application
{
    /// <summary>演示模式：使用内置的模拟布局，不读写真实硬件。</summary>
    public static bool IsDemo { get; private set; }

    /// <summary>`--navtest`：启动后自动在三个导航项间跳转，把选中项 / PageHost 挂载 /
    /// 覆盖层可见性写进诊断日志，用于无界面环境下验证导航栏与标尺门控。</summary>
    public static bool NavTest { get; private set; }

    /// <summary>`--glow`：启动即开启边缘泛光（演示模式也能用，方便试效果与截图验证）。</summary>
    public static bool GlowAtStart { get; private set; }

    /// <summary>`--glowtest 0..1`：强制所有接缝以固定强度常亮（截图验收用，不看鼠标位置）。-1 = 关闭。</summary>
    public static double GlowTestIntensity { get; private set; } = -1;

    /// <summary>`--arrange left|right|above|below`：把两台假显示器塞进真实桌面里最大的一块屏，
    /// 好在实机上把四个方位都截出图来。空串表示没指定。</summary>
    public static string Arrange { get; private set; } = "";

    /// <summary>`--page align|glow|dev2|settings`：启动后落到指定导航页（截图验收用，免模拟点击）。</summary>
    public static string InitialPage { get; private set; } = "";

    /// <summary>`--background`：开机自启的后台形态——主窗口不出现，
    /// 只留托盘 + 按存档恢复边缘泛光。主窗口仍会创建（拓扑/存档逻辑都在它身上），
    /// 只是 Loaded 后立刻隐藏。</summary>
    public static bool BackgroundStart { get; private set; }

    /// <summary>App 级边缘泛光服务（v1.6.0 起从 MainWindow 迁出）：
    /// 主窗口关到托盘后泛光继续有效。配置与存档由 MainWindow 负责同步。</summary>
    public static EdgeGlowService EdgeGlow { get; } = new();

    /// <summary>托盘"退出"时置真，绕过 MainWindow 关窗时"缩到托盘"的拦截。</summary>
    public static bool AllowExit { get; set; }

    /// <summary>单实例互斥体。挂字段防 GC 释放。</summary>
    private Mutex _single;

    /// <summary>托盘图标（WinForms NotifyIcon；Wpf.Ui 4.3.0 没有 NotifyIcon）。</summary>
    private System.Windows.Forms.NotifyIcon _tray;

    /// <summary>托盘气泡只弹一次（每次进托盘都弹会烦人）。</summary>
    private bool _balloonShown;

    /// <summary>测试/演示用的显示列表：--arrange 优先，否则用固定的 8 屏压力布局。</summary>
    public static List<DisplayInfo> DemoDisplays() =>
        Arrange.Length > 0 ? DisplayEnumerator.ArrangeDemo(Arrange) : DisplayEnumerator.Demo();

    private int _errorCount;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string[] args = Environment.GetCommandLineArgs();
        bool Has(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        string Value(string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return "";
        }

        Arrange = Value("--arrange");
        InitialPage = Value("--page");
        IsDemo = Has("--demo") || Arrange.Length > 0;      // --arrange 必然走模拟数据
        BackgroundStart = Has("--background");
        bool infoMode = Has("--info");
        bool geomCheck = Has("--geomcheck");
        NavTest = Has("--navtest");
        GlowAtStart = Has("--glow");
        if (double.TryParse(Value("--glowtest"), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double gti))
            GlowTestIntensity = Math.Clamp(gti, 0, 1);
        Diag.Enabled = Has("--diag") || NavTest;

        // 语言必须在任何窗口 / 资源被创建之前确定，
        // 因为 XAML 里的 {x:Static loc:Loc.Xxx} 在解析时就取值。
        Loc.Init(Value("--lang"));

        Diag.Reset();
        Diag.Log($"启动：demo={IsDemo} arrange={Arrange} background={BackgroundStart} info={infoMode} geom={geomCheck} " +
                 $"lang={Loc.Lang} cwd={AppContext.BaseDirectory}");

        // 任何未处理异常都落盘，便于在看不到界面的环境下定位问题。
        // 必须限制弹窗次数：若 MessageBox 自身也失败（无交互桌面），会不断重入直至栈溢出。
        DispatcherUnhandledException += (_, ex) =>
        {
            Diag.Log("未处理异常：" + ex.Exception);
            ex.Handled = true;

            _errorCount++;
            if (_errorCount > 3) { Shutdown(-1); return; }

            try
            {
                MessageBox.Show(ex.Exception.Message, "MonitorAlign 发生错误",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception boxEx)
            {
                Diag.Log("错误提示框也失败了：" + boxEx.Message);
                Shutdown(-1);
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Diag.Log("域异常：" + ex.ExceptionObject);

        try
        {
            ThemeManager.Apply();
            Diag.Log($"主题已应用：isLight={ThemeManager.EffectiveIsLight()}");
        }
        catch (Exception ex)
        {
            Diag.Log("主题应用失败：" + ex);
        }

        if (geomCheck)
        {
            Diag.Log("进入几何自检模式");
            Shutdown(SelfCheck.Run() == 0 ? 0 : 2);
            return;
        }

        if (infoMode)
        {
            RunInfoMode();
            Shutdown();
            return;
        }

        // 单实例：已有窗口就把它拉到前台，而不是再开一份。
        // --info / --geomcheck 不受影响（它们在上面已经返回）。
        // 互斥体要挂在字段上：一旦被 GC，句柄释放，第二个实例就能进来了。
        _single = new Mutex(true, "Local\\MonitorAlign.UTwelve.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Diag.Log("已有实例在运行：把它的窗口提到前台后退出");
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        // 已有实例可能藏在托盘（MainWindowHandle = 0，ShowWindow 摸不到它），
        // 挂一个命名事件让它自己把窗口亮出来；后台线程等，UI 线程不该阻塞。
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
                                            "Local\\MonitorAlign.UTwelve.Show");
        new Thread(() =>
        {
            try
            {
                while (showEvent.WaitOne())
                    Dispatcher.BeginInvoke(() => RestoreMainWindow());
            }
            catch { /* 应用退出时 WaitHandle 被释放，安静收场 */ }
        })
        { IsBackground = true }.Start();

        try
        {
            var w = new MainWindow(IsDemo);
            Diag.Log("MainWindow 实例已创建");

            if (BackgroundStart)
            {
                // 后台形态：窗口必须真的 Show 一次（Loaded/布局/存档恢复都靠它），
                // 但用户不该看见——Opacity=0 走完 Loaded 再藏，藏完只剩托盘。
                // 注意：Loaded 里直接 Hide() 会被 Show() 的收尾顶回可见（实测），
                // 必须延后到消息循环空闲时再藏。
                w.Opacity = 0;
                w.Loaded += (_, _) => w.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() =>
                    {
                        w.Hide();
                        w.Opacity = 1;
                        EnsureTray();
                        Diag.Log("后台模式：主窗口已隐藏，托盘就绪");
                    }));
                w.Show();
            }
            else
            {
                w.Show();
            }
            Diag.Log("MainWindow.Show() 已调用");
        }
        catch (Exception ex)
        {
            Diag.Log("MainWindow 创建失败：" + ex);
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        EdgeGlow.Stop();
        _tray?.Dispose();
        base.OnExit(e);
    }

    // ==================== 托盘 ====================

    /// <summary>建托盘图标（幂等）。菜单：打开 / 退出；双击 = 打开。</summary>
    public void EnsureTray()
    {
        if (_tray != null) return;
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = Loc.TrayTooltip,
            Icon = LoadTrayIcon(),
            Visible = true,
        };
        _tray.ContextMenuStrip = BuildTrayMenu();
        _tray.DoubleClick += (_, _) => RestoreMainWindow();
        Diag.Log("托盘图标已创建");
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var sri = Application.GetResourceStream(
                new Uri("pack://application:,,,/Resources/app.ico"));
            return new System.Drawing.Icon(sri.Stream);
        }
        catch (Exception ex)
        {
            Diag.Log("托盘图标加载失败，回落系统默认：" + ex.Message);
            return System.Drawing.SystemIcons.Application;
        }
    }

    private System.Windows.Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.TrayOpen, null, (_, _) => RestoreMainWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Loc.TrayExit, null, (_, _) => ExitForReal());
        return menu;
    }

    /// <summary>把主窗口从托盘里亮出来（双击托盘 / 菜单"打开" / 第二个实例来敲门）。</summary>
    public void RestoreMainWindow()
    {
        var w = MainWindow;
        if (w == null) return;
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    /// <summary>第一次缩进托盘时弹个气泡说明去向，之后不再打扰。</summary>
    public void ShowTrayBalloonOnce()
    {
        if (_balloonShown || _tray == null) return;
        _balloonShown = true;
        _tray.BalloonTipTitle = Loc.TrayBalloonTitle;
        _tray.BalloonTipText = Loc.TrayBalloonText;
        _tray.ShowBalloonTip(3000);
    }

    /// <summary>真退出：停泛光、收托盘、放行关窗、结束应用。</summary>
    public void ExitForReal()
    {
        AllowExit = true;
        EdgeGlow.Stop();
        _tray?.Dispose();
        _tray = null;
        if (MainWindow is MainWindow mw) mw.ForceClose();
        Shutdown();
    }

    // ==================== 开机自启（HKCU Run） ====================

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MonitorAlign";

    /// <summary>读开机自启状态（HKCU Run 里有没有我们的值）。</summary>
    public static bool GetAutoStart()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return k?.GetValue(RunValueName) is string;
        }
        catch (Exception ex)
        {
            Diag.Log("读取开机自启失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>写/删开机自启。带 --background：静默进托盘，泛光直接恢复。</summary>
    public static void SetAutoStart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (on)
                k.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --background");
            else
                k.DeleteValue(RunValueName, throwOnMissingValue: false);
            Diag.Log($"开机自启已{(on ? "开启" : "关闭")}");
        }
        catch (Exception ex)
        {
            Diag.Log("写入开机自启失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 自检模式：把探测结果与拓扑分析写到 exe 同目录 info.txt，
    /// 便于在没有多屏环境的机器上验证算法。
    /// </summary>
    /// <summary>
    /// 单实例：找到已在运行的那个自己，把它的主窗口提到前台。
    /// 找不到（比如旧实例只剩覆盖层）就安静退出，不弹窗打扰。
    /// </summary>
    private static void ActivateExistingInstance()
    {
        // 先敲命名事件：已存在的实例若藏在托盘（无可见主窗口），
        // 靠它自己亮出窗口——这条路径对隐藏窗口是唯一可靠的。
        try
        {
            if (EventWaitHandle.TryOpenExisting("Local\\MonitorAlign.UTwelve.Show",
                                                out var evt))
            {
                evt.Set();
                evt.Dispose();
            }
        }
        catch { }

        try
        {
            int pid = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
            {
                if (p.Id == pid || p.MainWindowHandle == IntPtr.Zero) continue;
                Win32.ShowWindow(p.MainWindowHandle, 9);        // SW_RESTORE
                Win32.SetForegroundWindow(p.MainWindowHandle);
                return;
            }
        }
        catch { }
    }

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
