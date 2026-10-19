using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using MonitorAlign.Core;
using MonitorAlign.Native;
using MonitorAlign.Overlay;
using MonitorAlign.Themes;

namespace MonitorAlign;

/// <summary>
/// 主窗口。基类是 Wpf.Ui 的 <see cref="Wpf.Ui.Controls.FluentWindow"/> —— 必须与
/// MainWindow.xaml 的根元素类型一致，否则分部类基类冲突编译不过。
/// 材质、圆角、标题栏、换肤都交给它，这里只管业务。
/// </summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly bool _isDemo;
    private List<DisplayInfo> _displays = new();
    private Topology _topo;
    private Dictionary<int, int> _depthMap = new();

    /// <summary>启动时的排列基线。全程只设一次，"撤销修改"就是回到这里。</summary>
    private List<MonitorPosition> _baseline = new();

    private Seam _currentSeam;

    private OverlayWindow _baseOverlay, _targetOverlay;
    private double? _measuredDelta;

    // ---- 锚点（两屏标尺共同的位置，基准侧可拖）----
    private double _anchorPrev;                       // 上一次同步给目标侧的锚点值

    // ---- 设置存档（演示模式不碰）----
    private AlignSettings _settings;
    private int _internalApplies, _handledApplies;    // 自己提交的排列变更计数（区别于外部改动）

    // ---- 缩略图上的标尺位置指示（两条短线）----
    private Rectangle _markBase, _markTarget;
    private double _topoL, _topoT, _topoScale, _topoOx, _topoOy;

    // ---- 导航：标尺只在「显示器对齐」页显示 ----
    private bool _alignPageActive = true;

    public MainWindow(bool isDemo)
    {
        InitializeComponent();
        _isDemo = isDemo;

        // 默认落在第二项（对齐页）。SelectedItem 没有公开 setter，
        // 唯一入口是 Navigate(TargetPageTag)。导航发生后会由 AlignPage
        // 把 PageHost 摘走挂成自己的内容，这里不用管。
        Loaded += (_, _) => Nav.Navigate(App.InitialPage.Length > 0 ? App.InitialPage : "align");

        SourceInitialized += (_, _) => InitWindowHooks();
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += (_, _) => SaveSettings();
        Loaded += (_, _) =>
        {
            RefreshAll();

            // 存档里的泛光参数灌进 App 级服务，再决定要不要自动起（演示模式不读档，永不自动开）
            SyncGlowConfig();
            if (App.GlowAtStart || (!_isDemo && _settings?.EdgeGlowEnabled == true))
                SetEdgeGlowEnabled(true);

            Diag.Log($"界面已加载：显示器 {_displays.Count} 台，接缝 {_topo?.Seams.Count ?? 0} 条，" +
                     $"链深上限 {_topo?.MaxDepth ?? 0}，当前接缝 {(_currentSeam == null ? "无" : _currentSeam.Label)}，" +
                     $"版本 {Loc.AppVersion}，实际尺寸 {ActualWidth:0}x{ActualHeight:0}，" +
                     $"画布宽 {TopologyCanvas.ActualWidth:0}");

            if (App.BackgroundStart) return;    // 后台形态：不弹标尺不跑自检，App 会把窗口藏掉

            // 启动即显示参考物，不再要求用户点按钮。
            // 用 Loaded 优先级延后一拍，确保布局与接缝下拉框先就绪。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_alignPageActive)
                    {
                        StartAlignment();
                        Diag.Log($"对齐会话已启动：基准窗口={_baseOverlay != null} 目标窗口={_targetOverlay != null}");
                    }
                }
                catch (Exception ex) { Diag.Log("启动对齐会话失败：" + ex); }
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            if (App.NavTest) _ = NavSelfTest();
        };
    }

    /// <summary>--navtest：自动跳转各导航项，验证页面缓存 / PageHost 挂载 / 标尺门控 / 新页面无异常。</summary>
    private async Task NavSelfTest()
    {
        await Task.Delay(1200);
        LogNav("起步");
        Nav.Navigate("glow");     await Task.Delay(700); LogNav("切到glow");
        Nav.Navigate("align");    await Task.Delay(700); LogNav("切回align");
        Nav.Navigate("dev2");     await Task.Delay(700); LogNav("切到dev2");
        Nav.Navigate("settings"); await Task.Delay(700); LogNav("切到settings");
        Nav.Navigate("align");    await Task.Delay(700); LogNav("再切回align");
        Diag.Log($"navtest 完成：AlignPage 实例数={Pages.AlignPage.InstanceCount}（应为 1）");
    }

    private void LogNav(string step)
    {
        var item = Nav.SelectedItem as Wpf.Ui.Controls.NavigationViewItem;
        Diag.Log($"navtest {step}：选中={item?.TargetPageTag} " +
                 $"PageHost父={PageHost.Parent?.GetType().Name ?? "(无)"} " +
                 $"基准覆盖层可见={_baseOverlay?.IsVisible} 目标覆盖层可见={_targetOverlay?.IsVisible} " +
                 $"窗口={ActualWidth:0}x{ActualHeight:0} 对齐页内容高={PageAlignContentHeight:0}");
    }

    // ==================== 导航栏 ====================
    //
    // 三项：实验功能（占位）/ 显示器对齐（主业）/ 扩展功能（占位）。
    // 页面切换由 NavigationView 自己导航完成，这里只管标尺的门控：
    // 标尺覆盖层只在对齐页显示 —— 切走 Hide，切回 Show（没建过就现建）。

    private void Nav_SelectionChanged(Wpf.Ui.Controls.NavigationView sender, RoutedEventArgs args)
    {
        bool align = (Nav.SelectedItem as Wpf.Ui.Controls.NavigationViewItem)?.TargetPageTag == "align";
        _alignPageActive = align;
        SetAlignActive(align);
    }

    private void SetAlignActive(bool active)
    {
        if (active)
        {
            if (_baseOverlay == null)
            {
                if (_currentSeam != null) StartAlignment();
            }
            else
            {
                _baseOverlay.Show();
                _targetOverlay?.Show();
            }
        }
        else
        {
            _baseOverlay?.Hide();
            _targetOverlay?.Hide();
        }
    }

    // ==================== 功能一：边缘泛光 ====================
    //
    // 服务本体在 App.EdgeGlow（App 级）：主窗口关到托盘后它还得活着。
    // 这里只负责：拓扑变化时喂数据、把存档参数灌进服务、给设置页当入口。

    /// <summary>泛光开关状态（设置页开关据此回填）。</summary>
    public bool EdgeGlowEnabled => App.EdgeGlow.Enabled;

    /// <summary>设置页开关调用：启停服务并写存档（演示模式不写档）。</summary>
    public void SetEdgeGlowEnabled(bool on)
    {
        if (on) App.EdgeGlow.Start(_topo?.Seams ?? new List<Seam>());
        else App.EdgeGlow.Stop();

        if (!_isDemo && _settings != null && _settings.EdgeGlowEnabled != on)
        {
            _settings.EdgeGlowEnabled = on;
            SaveSettings();
        }
    }

    /// <summary>把存档里的泛光参数灌进服务配置（启动时，在自动起泛光之前）。</summary>
    private void SyncGlowConfig()
    {
        if (_isDemo || _settings == null) return;
        var c = App.EdgeGlow.Config;
        c.Sensitivity = _settings.EdgeGlowSensitivity > 0 ? _settings.EdgeGlowSensitivity : 1.0;
        c.Brightness = Math.Clamp(_settings.EdgeGlowBrightness > 0 ? _settings.EdgeGlowBrightness : 0.6, 0.05, 1);
        c.Distance = Math.Clamp(_settings.EdgeGlowDistance > 0 ? _settings.EdgeGlowDistance : 96, 24, 320);
        c.Colors = new Dictionary<string, string>(_settings.EdgeGlowColors ?? new());
    }

    /// <summary>设置页（泛光页）要用的只读视图。</summary>
    public IReadOnlyList<Seam> AllSeams => _topo?.Seams;

    /// <summary>设置存档（泛光页把参数改动写回这里；演示模式下为 null）。</summary>
    public AlignSettings Settings => _settings;

    /// <summary>落盘存档（泛光页用；演示模式不写）。</summary>
    public void SaveSettingsFile() => SaveSettings();

    /// <summary>对齐页内容的当前高度。占位/设置页用它做 MinHeight，
    /// 窗口 SizeToContent 时切页尺寸才不抖（用户明确要求：窗口不固定，但切页不能变）。</summary>
    public double PageAlignContentHeight => PageAlign.DesiredSize.Height;

    // ==================== 窗口钩子 ====================
    //
    // 材质（Mica）与圆角由 Wpf.Ui 的 FluentWindow 负责 —— XAML 里的
    // WindowBackdropType="Mica" / WindowCornerPreference="Round" / ExtendsContentIntoTitleBar="True"
    // 已经把它调好了，我们不再自己碰 DWM。
    //
    // 这里只挂一个消息钩子，用来响应"外部改了显示器排列"和"系统换了主题"。

    private void InitWindowHooks()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        int style = Win32.GetWindowLong(hwnd, Win32.GWL_STYLE);
        Diag.Log($"窗口句柄=0x{hwnd:X} style=0x{style:X8} " +
                 $"WS_CAPTION={(style & Win32.WS_CAPTION) != 0}（系统材质能画出来的硬前提）");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_SETTINGCHANGE)
        {
            // 主题可能被系统改了；Auto 模式下重新套一遍（Wpf.Ui 会连 Mica 明暗一起换）
            Dispatcher.BeginInvoke(() =>
            {
                ThemeManager.RefreshFromSystem();
                RenderTopology();
            });
        }
        else if (msg == Win32.WM_DISPLAYCHANGE)
        {
            // 排列被外部改变（驱动重载 / 插拔 / 休眠唤醒）
            Dispatcher.BeginInvoke(RefreshAll);
        }
        return IntPtr.Zero;
    }

    // ==================== 数据刷新 ====================

    private void RefreshAll()
    {
        _displays = _isDemo ? App.DemoDisplays() : DisplayEnumerator.Enumerate();

        // 基线只在第一次建立：之后即使外部改动了排列，"撤销修改"依然回到启动前
        if (_baseline.Count == 0) _baseline = DisplayArrangement.Snapshot(_displays);

        RefreshSettings();

        _measuredDelta = null;
        Reanalyze();
        UpdateReadouts();
    }

    /// <summary>
    /// 设置存档的装载与失效。签名含位置：用户在外面挪过屏就算"排列与记录不符"，重置存档。
    /// 但自己提交的补偿也会改位置 —— 用内部提交计数区分，自己的改动只更新签名不重置。
    /// 演示模式一律不读不写不重置，保护真机存档。
    /// </summary>
    private void RefreshSettings()
    {
        if (_isDemo) return;

        string sig = SettingsStore.ComputeSignature(_displays);
        bool internalChange = _handledApplies < _internalApplies;
        _handledApplies = _internalApplies;

        if (_settings == null)
        {
            _settings = SettingsStore.Load();
            if (_settings != null && _settings.Signature != sig && !internalChange)
            {
                SettingsStore.Reset();
                Diag.Log("排列与设置存档不符，存档已重置");
                _settings = null;
            }
            _settings ??= new AlignSettings { Signature = sig };
            if (_settings.Signature != sig) { _settings.Signature = sig; SettingsStore.Save(_settings); }
            return;
        }

        if (_settings.Signature == sig) return;

        if (internalChange)
        {
            // 自己提交导致的位置变化：签名跟着走，锚点保留
            _settings.Signature = sig;
            SettingsStore.Save(_settings);
        }
        else
        {
            SettingsStore.Reset();
            Diag.Log("排列被外部改变，设置存档已重置");
            _settings = new AlignSettings { Signature = sig };
        }
    }

    private void SaveSettings()
    {
        if (_isDemo || _settings == null) return;
        SettingsStore.Save(_settings);
    }

    private static string SeamKey(Seam s) => $"{s.Base.Index}->{s.Target.Index}";    private void Reanalyze()
    {
        _topo = TopologyAnalyzer.Analyze(_displays);
        BuildDepthMap();
        RenderTopology();
        FillSeamCombo();

        // 拓扑变了，泛光覆盖层跟着重建（未启用时此调用只清不建）
        App.EdgeGlow.UpdateSeams(_topo?.Seams ?? new List<Seam>());
    }

    private void BuildDepthMap()
    {
        _depthMap = new Dictionary<int, int>();
        if (_topo?.Primary != null) _depthMap[_topo.Primary.Index] = 0;
        foreach (var s in _topo.Seams)
            if (!s.Unreachable)
                _depthMap[s.Target.Index] = s.Depth;
    }

    /// <summary>画布宽度跟着卡片走，宽度变了要重画。</summary>
    private void TopologyCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 只对宽度变化反应：RenderTopology 自己会改 Height，不加这个判断会来回抖。
        if (!e.WidthChanged) return;
        Dispatcher.BeginInvoke(new Action(RenderTopology),
                               System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RenderTopology()
    {
        TopologyCanvas.Children.Clear();
        if (_topo == null || _displays.Count == 0) return;

        var (l, t, w, h) = _topo.Bounds();
        w = Math.Max(w, 1);
        h = Math.Max(h, 1);

        // 画布宽度撑满卡片，首次布局前 ActualWidth 还是 0，给个保守值顶上
        double cw = TopologyCanvas.ActualWidth;
        if (cw <= 1) cw = 400;

        // 画布高度跟着排列的宽高比走，否则常见的"一横一竖"布局会被上下裁到很窄，
        // 左右留出大片空白，看起来像"图画小了"。窗口是 SizeToContent，高度会自动跟上。
        TopologyCanvas.Height = Math.Clamp(cw * h / w, 56, 176);

        double pad = 3;
        double scale = Math.Min((cw - pad * 2) / w,
                                (TopologyCanvas.Height - pad * 2) / h);
        double ox = pad + (cw - pad * 2 - w * scale) / 2;
        double oy = pad + (TopologyCanvas.Height - pad * 2 - h * scale) / 2;

        foreach (var d in _displays)
        {
            int depth = _depthMap.TryGetValue(d.Index, out int v) ? v : 0;
            var fill = Res(depth == 0 ? "MaTopoPrimaryFillBrush" : depth == 1 ? "MaTopoDepth1FillBrush" : "MaTopoDepth2FillBrush");
            var stroke = Res(depth == 0 ? "MaTopoPrimaryStrokeBrush" : depth == 1 ? "MaTopoDepth1StrokeBrush" : "MaTopoDepth2StrokeBrush");

            double rw = Math.Max(d.Width * scale, 3);
            double rh = Math.Max(d.Height * scale, 3);
            double rx = (d.Left - l) * scale + ox;
            double ry = (d.Top - t) * scale + oy;

            var rect = new Rectangle
            {
                Width = rw, Height = rh,
                Fill = fill, Stroke = stroke, StrokeThickness = 1,
                RadiusX = 2, RadiusY = 2,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(rect, rx);
            Canvas.SetTop(rect, ry);
            TopologyCanvas.Children.Add(rect);

            if (rw >= 16 && rh >= 13)
            {
                var label = new TextBlock
                {
                    Text = d.Index.ToString(),
                    FontSize = 10,
                    Foreground = stroke,
                    Width = rw,
                    TextAlignment = TextAlignment.Center
                };
                Canvas.SetLeft(label, rx);
                Canvas.SetTop(label, ry + rh / 2 - 8);
                TopologyCanvas.Children.Add(label);
            }
        }

        DrawSeamChips(l, t, scale, ox, oy);

        // 标尺位置指示：当前接缝上的两条短线（基准/可动），随标尺拖动实时移动
        _topoL = l; _topoT = t; _topoScale = scale; _topoOx = ox; _topoOy = oy;
        _markBase = new Rectangle
        {
            Fill = Res("MaTopoPrimaryStrokeBrush"), RadiusX = 1, RadiusY = 1
        };
        _markTarget = new Rectangle
        {
            Fill = Res("MaTopoDepth1StrokeBrush"), RadiusX = 1, RadiusY = 1
        };
        Panel.SetZIndex(_markBase, 4);
        Panel.SetZIndex(_markTarget, 4);
        TopologyCanvas.Children.Add(_markBase);
        TopologyCanvas.Children.Add(_markTarget);
        UpdateThumbnailMarks();
    }

    /// <summary>把两侧标尺的当前位置画成当前接缝上的两条短线；没有会话时隐藏。</summary>
    private void UpdateThumbnailMarks()
    {
        if (_markBase == null || _markTarget == null) return;
        var s = _currentSeam;
        if (s == null || s.Unreachable || _baseOverlay == null || _targetOverlay == null)
        {
            _markBase.Width = _markBase.Height = 0;
            _markTarget.Width = _markTarget.Height = 0;
            return;
        }

        double mb = _baseOverlay.Marker, mt = _targetOverlay.Marker;
        if (s.Vertical)
        {
            double sx = (s.SeamCoord - _topoL) * _topoScale + _topoOx;
            double yb = (mb - _topoT) * _topoScale + _topoOy;
            double yt = (mt - _topoT) * _topoScale + _topoOy;
            SetMark(_markBase, sx - 9, yb - 1, 7, 2);
            SetMark(_markTarget, sx + 2, yt - 1, 7, 2);
        }
        else
        {
            double sy = (s.SeamCoord - _topoT) * _topoScale + _topoOy;
            double xb = (mb - _topoL) * _topoScale + _topoOx;
            double xt = (mt - _topoL) * _topoScale + _topoOx;
            SetMark(_markBase, xb - 1, sy - 9, 2, 7);
            SetMark(_markTarget, xt - 1, sy + 2, 2, 7);
        }

        static void SetMark(Rectangle r, double x, double y, double w, double h)
        {
            r.Width = w; r.Height = h;
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
        }
    }

    /// <summary>
    /// 在缩略图上画"接缝点"：每条接缝的中点一枚可点击的圆点。
    /// 选中的用强调色实心，其余中性描边；孤屏画虚线警示点（点了只弹提示）。
    /// </summary>
    private void DrawSeamChips(double boundL, double boundT, double scale, double ox, double oy)
    {
        if (_topo == null) return;
        double dot = 9;                       // 视觉圆点直径
        double hit = 26;                      // 可点区域直径（比视觉大，好点中）

        foreach (var s in _topo.Seams)
        {
            // 接缝中点（虚拟桌面逻辑坐标）→ 画布坐标
            double lx = s.Vertical ? s.SeamCoord : s.MidPoint;
            double ly = s.Vertical ? s.MidPoint : s.SeamCoord;
            double cx = (lx - boundL) * scale + ox;
            double cy = (ly - boundT) * scale + oy;

            bool selected = !s.Unreachable && ReferenceEquals(s, _currentSeam);

            // 配色遵守拓扑图同一套语言：选中 = 实心强调色（一眼看出正在对齐哪条），
            // 其余 = 中性描边空心；孤屏再叠加虚线描边表示"不可用"。
            var visual = new Ellipse
            {
                Width = dot, Height = dot,
                Fill = selected ? Res("MaTopoPrimaryStrokeBrush") : Res("MaTopoDepth1FillBrush"),
                Stroke = selected ? Res("MaTopoPrimaryStrokeBrush") : Res("MaTopoDepth1StrokeBrush"),
                StrokeThickness = 1.5,
                StrokeDashArray = s.Unreachable ? new DoubleCollection { 2, 2 } : null,
                Opacity = s.Unreachable ? 0.8 : 1
            };
            Canvas.SetLeft(visual, cx - dot / 2);
            Canvas.SetTop(visual, cy - dot / 2);
            Panel.SetZIndex(visual, 5);
            TopologyCanvas.Children.Add(visual);

            // 半径 hit 的透明命中区，别让用户拿 9px 的小点练手
            Seam captured = s;
            var hitArea = new Ellipse
            {
                Width = hit, Height = hit,
                Fill = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = SeamText(s)
            };
            hitArea.MouseLeftButtonDown += (_, e) =>
            {
                SeamChip_Click(captured);
                e.Handled = true;
            };
            Canvas.SetLeft(hitArea, cx - hit / 2);
            Canvas.SetTop(hitArea, cy - hit / 2);
            Panel.SetZIndex(hitArea, 6);
            TopologyCanvas.Children.Add(hitArea);
        }
    }

    // ==================== 接缝选择 ====================
    //
    // 不用下拉框了：缩略图上每条接缝的中点画一个可点击的"接缝点"，
    // 选中的是强调色，点它就切换 —— 位置本身就在图上，比下拉直观。
    // 这里只负责选中态与文字说明。

    private void FillSeamCombo()
    {
        // 保留原来的选中对象（外部改动排列后拓扑会重建，尽量别把用户的选择顶掉）；
        // 首次没有"原来的选择"时，回落到存档里上次选中的那条接缝。
        int keep = _currentSeam?.Target.Index ?? _settings?.SelectedTarget ?? -1;
        var seams = _topo?.Seams ?? new List<Seam>();

        _currentSeam = seams.FirstOrDefault(s => !s.Unreachable && s.Target.Index == keep)
                       ?? seams.FirstOrDefault(s => !s.Unreachable);

        UpdateSeamLabel();
        RenderTopology();            // 接缝点的高亮要跟着选中态变
        UpdateReadouts();
    }

    /// <summary>缩略图接缝点被点击。</summary>
    private void SeamChip_Click(Seam seam)
    {
        if (seam.Unreachable)
        {
            TxtStatus.Text = Loc.StatusUnreachable;
            return;
        }
        if (ReferenceEquals(seam, _currentSeam)) return;

        _currentSeam = seam;
        _measuredDelta = null;
        UpdateSeamLabel();
        RenderTopology();
        UpdateReadouts();
        StartAlignment();            // 换接缝 = 把标尺挪到新接缝
        if (!_isDemo && _settings != null)
        {
            _settings.SelectedTarget = seam.Target.Index;
            SaveSettings();
        }
        Diag.Log($"切换接缝：#{seam.Base.Index} → #{seam.Target.Index}");
    }

    private void UpdateSeamLabel()
    {
        // 接缝本身的信息（编号/方向/深度）在接缝点的 ToolTip 里，这里不再重复；
        // 这一行只留"怎么切换"的操作说明，单接缝时没有可切换的东西，留空。
        TxtSeamLabel.Text = (_topo?.Seams.Count ?? 0) > 1 ? Loc.SeamPickHint : "";
    }

    /// <summary>接缝的显示文案（工具提示 / 颜色列表行共用）。</summary>
    public static string SeamText(Seam s) =>
        s.Unreachable
            ? Loc.Fmt(Loc.ComboUnreachable, s.Target.Index)
            : Loc.Fmt(Loc.ComboItem, s.Label, s.DirectionText, s.Depth);

    // ==================== 读数 ====================

    private void UpdateReadouts()
    {
        var s = _currentSeam;

        if (s == null || s.Unreachable)
        {
            TxtError.Text = Loc.Dash;
            TxtApplied.Text = Loc.ZeroPx;
            TxtScale.Text = "";
        }
        else
        {
            TxtError.Text = _measuredDelta.HasValue
                ? Loc.Px((int)Math.Round(_measuredDelta.Value))
                : Loc.Dash;

            var b = _baseline.FirstOrDefault(p => p.DeviceName == s.Target.DeviceName);
            int off = b == null ? 0
                    : s.Vertical ? s.Target.PositionY - b.Y
                                 : s.Target.PositionX - b.X;
            TxtApplied.Text = Loc.Px(off);

            // 物理换算：沿接缝方向的 1 逻辑像素 = 多少毫米。
            // 用户据此判断"还差 2px"到底肉眼看得出看不出。
            double mmPerPx = s.Vertical ? s.Target.MmPerPixelY : s.Target.MmPerPixelX;
            TxtScale.Text = mmPerPx > 0 ? Loc.MmPerPixel(mmPerPx) : "";
        }

        BtnUndo.IsEnabled = HasChanges();
    }

    /// <summary>当前排列是否已偏离启动时的基线。</summary>
    private bool HasChanges()
    {
        foreach (var d in _displays)
        {
            var b = _baseline.FirstOrDefault(p => p.DeviceName == d.DeviceName);
            if (b == null) continue;
            if (d.PositionX != b.X || d.PositionY != b.Y) return true;
        }
        return false;
    }

    // ==================== 对齐会话 ====================

    private void StartAlignment()
    {
        var s = _currentSeam;
        if (s == null)
        {
            TxtStatus.Text = Loc.StatusNoSeam;
            return;
        }
        if (s.Unreachable)
        {
            TxtStatus.Text = Loc.StatusUnreachable;
            return;
        }

        StopOverlay();
        _measuredDelta = null;

        _baseOverlay = new OverlayWindow(s.Base, s, true);
        _targetOverlay = new OverlayWindow(s.Target, s, false);

        // 存档里有这条接缝的锚点 → 恢复（两屏标尺一起回到上次的位置，待提交增量为 0：
        // 对齐结果早已写进系统，不该再算一遍）
        if (!_isDemo && _settings != null &&
            _settings.Anchors.TryGetValue(SeamKey(s), out double savedAnchor))
        {
            _baseOverlay.SetMarkerSilent(savedAnchor);
            _targetOverlay.AdoptAnchor(savedAnchor);
            Diag.Log($"恢复锚点：{SeamKey(s)} = {savedAnchor:0}");
        }

        _targetOverlay.MarkerChanged += OnMarkerChanged;
        _targetOverlay.CommitRequested += OnCommitRequested;
        // Esc 与唯一按钮同义：撤销到启动前。
        // （不再用它"结束会话"——没有「开始对齐」按钮了，结束就再也起不来。）
        _targetOverlay.CloseRequested += UndoToLaunch;
        _baseOverlay.CloseRequested += UndoToLaunch;

        // 基准侧拖的是锚点：两屏标尺一起挪，不写系统
        _anchorPrev = _baseOverlay.Marker;
        _baseOverlay.MarkerChanged += OnAnchorChanged;
        _baseOverlay.AnchorSettled += OnAnchorSettled;
        _baseOverlay.ResetRequested += OnAnchorReset;

        _baseOverlay.Show();
        _targetOverlay.Show();
        UpdateThumbnailMarks();

        TxtStatus.Text = Loc.StatusIdle;
    }

    /// <summary>基准侧拖动中：把锚点位移同步给目标侧（待提交增量不变）。</summary>
    private void OnAnchorChanged(double marker)
    {
        double delta = marker - _anchorPrev;
        _anchorPrev = marker;
        _targetOverlay?.ShiftAnchor(delta);
        UpdateThumbnailMarks();
    }

    /// <summary>基准侧拖动/键盘结束：把锚点写进存档。</summary>
    private void OnAnchorSettled(double marker)
    {
        if (_isDemo || _settings == null || _currentSeam == null) return;
        _settings.Anchors[SeamKey(_currentSeam)] = marker;
        SaveSettings();
        Diag.Log($"锚点已保存：{SeamKey(_currentSeam)} = {marker:0}");
    }

    /// <summary>基准侧点"回到居中"：锚点回接缝中点，目标侧同步平移。</summary>
    private void OnAnchorReset()
    {
        var s = _currentSeam;
        if (s == null || _baseOverlay == null) return;

        double delta = s.MidPoint - _baseOverlay.Marker;
        _baseOverlay.SetMarkerSilent(s.MidPoint);
        _anchorPrev = s.MidPoint;
        _targetOverlay?.ShiftAnchor(delta);
        UpdateThumbnailMarks();
        Diag.Log($"锚点已重置到居中：{SeamKey(s)} = {s.MidPoint}");

        if (!_isDemo && _settings != null)
        {
            _settings.Anchors.Remove(SeamKey(s));   // 居中 = 默认值，不必留档
            SaveSettings();
        }
    }

    private void OnMarkerChanged(double marker)
    {
        // 显示"自上次提交以来的增量"（PendingDelta）。
        // 绝不能用实时接缝中点：提交后排列变了、中点会漂移，读数会凭空变大。
        if (_targetOverlay == null) return;
        _measuredDelta = _targetOverlay.PendingDelta;
        UpdateReadouts();
        UpdateThumbnailMarks();
    }

    /// <summary>防抖后触发：把拖动结果换算成逻辑偏移并写入系统。</summary>
    private void OnCommitRequested(double marker)
    {
        var s = _currentSeam;
        if (s == null || s.Unreachable || _targetOverlay == null) return;

        // 只提交"自上次成功提交以来的增量"（OverlayWindow 里记账）。
        // 曾经这里算 marker − 实时中点：提交→排列变→中点漂移→下一次提交把
        // 之前的应用过的量再算一遍，连点 5 次 ▼ 实际写了 52px（日志 1,3,7,15）。
        int delta = _targetOverlay.PendingDelta;
        if (delta == 0) { UpdateReadouts(); return; }

        var target = s.Target;
        var (nx, ny) = AlignmentSolver.SolvePosition(s.Vertical, target.PositionX, target.PositionY, delta);

        var positions = _displays.Select(d =>
            d.DeviceName == target.DeviceName
                ? new MonitorPosition(d.DeviceName, nx, ny)
                : new MonitorPosition(d.DeviceName, d.PositionX, d.PositionY)).ToList();

        if (DisplayArrangement.WouldProduceNegativeOrigin(positions))
            TxtStatus.Text = Loc.StatusNegativeOrigin;

        _internalApplies++;   // 接下来的 WM_DISPLAYCHANGE 是我们自己引起的，存档签名跟着走即可
        bool ok = DisplayArrangement.Apply(_displays, positions, _isDemo, out string err);
        if (!ok)
        {
            TxtStatus.Text = Loc.StatusApplyFailed + err;
            Diag.Log($"提交补偿失败：delta={delta} → ({nx},{ny})  {err}");
            return;
        }

        // 真机上没法看界面时，这行日志是"补偿到底写进系统没有"的唯一证据
        Diag.Log($"提交补偿：#{target.Index} delta={delta} → ({nx},{ny})" +
                 (_isDemo ? "（演示，未写系统）" : "（已写系统）"));

        // 记账：下一次提交只算新的增量（失败时不记，重试仍按本次增量）
        _targetOverlay.MarkCommitted();
        _measuredDelta = 0;
        UpdateReadouts();
        TxtStatus.Text = Loc.Fmt(Loc.StatusApplied, target.Index, Loc.Px(-delta));
    }

    private void StopOverlay()
    {
        if (_baseOverlay != null) { _baseOverlay.Close(); _baseOverlay = null; }
        if (_targetOverlay != null) { _targetOverlay.Close(); _targetOverlay = null; }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
    }

    // ==================== 唯一操作：撤销到启动前 ====================

    private void BtnUndo_Click(object sender, RoutedEventArgs e) => UndoToLaunch();

    private void UndoToLaunch()
    {
        if (_baseline.Count == 0) return;

        _internalApplies++;   // 撤销也是我们引起的排列变更，存档签名跟着走即可
        bool ok = DisplayArrangement.Apply(_displays, _baseline, _isDemo, out string err);
        Diag.Log($"撤销到启动前：{(ok ? "成功" : "失败 " + err)}");

        // 排列回到起点，但标尺还停在拖过的位置——必须把提交零点也重置，
        // 否则下一次提交会把"已撤销的那段"再算一遍。
        // 零点 = 当前锚点（基准标尺的位置），不是接缝中点。
        _targetOverlay?.ResetCommitBaseline(_baseOverlay?.Marker ?? _currentSeam?.MidPoint ?? 0);

        _measuredDelta = null;
        Reanalyze();
        UpdateReadouts();
        TxtStatus.Text = ok ? Loc.StatusUndone : Loc.StatusUndoFailed + err;
    }

    // ==================== 主题 / 关于（v1.6.0 起住设置页） ====================

    /// <summary>设置页主题切换后调用：拓扑图用的 Ma* 画刷要重画、泛光缺省强调色要重建。</summary>
    public void OnThemeChanged()
    {
        RenderTopology();
        App.EdgeGlow.RefreshColors();
    }

    // ==================== 键盘与关窗 ====================

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            UndoToLaunch();
            e.Handled = true;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 泛光开着时，关窗 ≠ 退出：缩到托盘后台常驻（用户明确要求"软件关了也有效"）。
        // 真退出走托盘菜单，或先把泛光关掉再关窗。
        // 演示模式不常驻，直接关。
        if (!App.AllowExit && !_isDemo && App.EdgeGlow.Enabled)
        {
            e.Cancel = true;
            Hide();
            (Application.Current as App)?.EnsureTray();
            (Application.Current as App)?.ShowTrayBalloonOnce();
            Diag.Log("泛光运行中：主窗口缩到托盘");
            return;
        }

        // 不再弹确认框：改动已经实时写进系统、屏幕上肉眼可见，想反悔就点「撤销修改」。
        // 关闭即代表采纳。
        App.EdgeGlow.Stop();
        StopOverlayOnly();
        base.OnClosing(e);
    }

    /// <summary>托盘"退出"用：绕过"缩到托盘"直接真关。</summary>
    public void ForceClose()
    {
        App.AllowExit = true;
        StopOverlayOnly();
        Close();
    }

    private void StopOverlayOnly()
    {
        if (_baseOverlay != null) { _baseOverlay.Close(); _baseOverlay = null; }
        if (_targetOverlay != null) { _targetOverlay.Close(); _targetOverlay = null; }
    }

    private static Brush Res(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
