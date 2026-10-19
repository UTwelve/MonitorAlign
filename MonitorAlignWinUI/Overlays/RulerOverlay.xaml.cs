using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MonitorAlign.Core;
using MonitorAlign.Native;
using System.Text;
using Windows.System;
using Windows.UI;

namespace MonitorAlign.Overlay;

/// <summary>
/// 单台显示器上的「对齐标尺」覆盖层。
///
/// 几何全部来自 <see cref="RulerGeometry"/>（纯函数、四方位共用一份判定），
/// 这里只负责把 (across, along) 映射成屏幕 (x, y, w, h) 再摆元素 ——
/// 与 WPF 版同一套约定，因为 <c>--geomcheck</c> 的 156 项断言就是按这套约定写的。
///
/// 位置记账也与 WPF 版逐字同源（<see cref="PendingDelta"/>）：
/// 提交只算"自上次提交以来的增量"，任何提交序列下都收敛。
///
/// 局部坐标轴（以"本屏"为参照，窗口左上角为原点，单位 DIP）：
///   across = 垂直于接缝（左右并排 → 屏幕 X；上下叠放 → 屏幕 Y）
///   along  = 沿接缝   （左右并排 → 屏幕 Y；上下叠放 → 屏幕 X）
///
/// ⚠ 换轴只许在 <see cref="RectOf"/> 里做一次。曾经"只换左上角、忘了换宽高"，
/// 横缝（across = 屏幕 Y）上的标签就跑到别处去了 ——
/// 与 RulerGeometry 立的那条规矩同类：一处判定，不许各写一份。
///
/// 按钮条（v1.6.0 起的形状，与 WPF 版一致）：
///   基准 <c>[≡][↺]</c> 沿 across 并排、拖动键靠接缝；目标 <c>[▲][≡][▼]</c> 沿 along 三键成串。
///   标签与读数一律排在条沿轴外侧 —— 旧版让标签骑线，与线中点的手柄必然重叠，
///   这就是"标尺文字被遮挡"的根因。
/// </summary>
public sealed partial class RulerOverlay : OverlayWindowBase
{
    private readonly DisplayInfo _display;
    private readonly Seam _seam;
    private readonly bool _isBase;
    private readonly double _scale;

    // 会话内固定的几何快照：补偿会移动目标屏的逻辑矩形，引用实时坐标会让
    // 接缝重叠区间与 MidPoint 漂移，多次微调就累积误差（WPF 版踩过）。
    private readonly int _acrossOrigin, _alongOrigin;
    private readonly int _seamCoord, _midPoint;
    private readonly int _left, _top, _right, _bottom;
    private readonly int _overlapStart, _overlapEnd;

    /// <summary>标尺沿接缝方向的位置（物理像素，与 Seam 坐标系一致）。</summary>
    private double _marker;

    /// <summary>上次成功提交时的位置 —— 提交只算"自这里以来的增量"。</summary>
    private double _lastCommitted;

    private bool _dragging;
    private double _dragLastAlong;
    private int _clampHits;
    private int _moveLogs;      // 拖动位移日志计数（只打前几次，见 OnDragMoved）
    private bool _releasing;    // 是否正在 OnDragReleased 里放捕获（见 OnDragCaptureLost）

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _commitTimer;

    // ---------------- 事件（与 WPF 版 OverlayWindow 同名同语义） ----------------

    /// <summary>标尺位置变化（沿接缝方向的物理像素）。</summary>
    public event Action<double> MarkerChanged;
    /// <summary>防抖后请求把当前位置写入系统。</summary>
    public event Action<double> CommitRequested;
    /// <summary>基准侧拖动/键盘移动锚点结束（不写系统，仅用于持久化与联动）。</summary>
    public event Action<double> AnchorSettled;
    /// <summary>基准侧点了"回到居中"。</summary>
    public event Action ResetRequested;
    /// <summary>用户按 Esc 请求退出对齐。</summary>
    public event Action CloseRequested;

    public RulerOverlay(DisplayInfo display, Seam seam, bool isBase)
    {
        InitializeComponent();

        _display = display;
        _seam = seam;
        _isBase = isBase;
        _scale = display.Scale <= 0 ? 1 : display.Scale;

        _left = display.Left;
        _top = display.Top;
        _right = display.Right;
        _bottom = display.Bottom;
        _seamCoord = seam.SeamCoord;
        _midPoint = seam.MidPoint;
        _overlapStart = seam.OverlapStart;
        _overlapEnd = seam.OverlapEnd;

        _acrossOrigin = seam.Vertical ? _left : _top;
        _alongOrigin = seam.Vertical ? _top : _left;

        _marker = _midPoint;
        _lastCommitted = _midPoint;      // 会话开始时没有任何待提交的增量

        //
        // 拖动的三条后续事件（移动 / 松手 / 捕获丢失）一律挂在 **Root** 上，
        // 捕获也抓在 Root 上 —— 不是挂在按钮条或拖动键上。
        //
        // ⚠ 这是实拍逼出来的：Render() 每帧都 Root.Children.Clear() 重建整棵树
        // （标尺在动，元素得跟着挪），抓住指针的那枚按钮**当帧就被销毁**，
        // 捕获随之丢失 —— 表现同样是"起手后第一帧就拖动结束"。
        // Root 是画布本身，永远不会被重建，捕获挂它身上才跨得过重建。
        Root.PointerMoved += OnDragMoved;
        Root.PointerReleased += OnDragReleased;
        Root.PointerCaptureLost += OnDragCaptureLost;

        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _commitTimer = dq.CreateTimer();
        _commitTimer.Interval = TimeSpan.FromMilliseconds(320);
        _commitTimer.Tick += (_, _) => { _commitTimer.Stop(); CommitRequested?.Invoke(_marker); };

        // 标尺层要键盘（方向键微调 + Esc 退出），所以不能被 NOACTIVATE 挡住激活
        InitOverlay(noActivate: false);
    }

    /// <summary>当前标尺位置（物理像素）。</summary>
    public double Marker => _marker;

    /// <summary>
    /// 自上次成功提交以来的增量（物理像素）—— 下一次提交就应用这么多。
    ///
    /// 不能用"marker − 接缝中点"：提交会改变排列，实时接缝中点会漂移，
    /// 连点几次微调就会叠加成 1+2+3+…（实测 5 次点击写了 52px）。
    /// 增量记账在任何提交序列下都收敛：已应用总量恒等于"标尺总位移"。
    /// </summary>
    public int PendingDelta => (int)Math.Round(_marker - _lastCommitted);

    /// <summary>提交成功后由外部调用：把当前位置记为新的零点。</summary>
    public void MarkCommitted()
    {
        _lastCommitted = _marker;
        Render();                       // 读数浮标归零，与"两条线已对齐"的视觉一致
    }

    /// <summary>
    /// 撤销到启动前之后调用：排列回去了，但标尺还停在拖过的位置，
    /// 必须把零点也重置，否则下一次提交会漏算这段已撤销的量。
    /// 零点不是接缝中点而是**当前锚点**（基准标尺的位置）。
    /// </summary>
    public void ResetCommitBaseline(double anchor)
    {
        _lastCommitted = anchor;
        Render();
    }

    /// <summary>外部（微调按钮/键盘）推动标尺，单位物理像素。</summary>
    public void Nudge(int delta)
    {
        SetMarker(_marker + delta);
        if (_isBase) AnchorSettled?.Invoke(_marker);   // 基准侧动的是锚点，不写系统
        else ScheduleCommit();
    }

    /// <summary>外部把标尺直接设到指定位置（不触发任何事件）。</summary>
    public void SetMarkerSilent(double value)
    {
        _marker = Clamp(value);
        Render();
    }

    /// <summary>恢复存档的锚点：位置与提交零点一起设，待提交增量归零。</summary>
    public void AdoptAnchor(double value)
    {
        SetMarkerSilent(value);
        _lastCommitted = _marker;
    }

    /// <summary>基准侧移动锚点时同步平移本侧：位置与零点一起挪，增量保持不变。</summary>
    public void ShiftAnchor(double deltaLogical)
    {
        _marker += deltaLogical;
        _lastCommitted += deltaLogical;
        Render();
    }

    /// <summary>关窗前停掉定时器（不然它会在窗口销毁后触发提交），并卸掉线程消息钩子。</summary>
    public void Shutdown()
    {
        _commitTimer.Stop();
        UnhookThreadKeyboard();
    }

    // ---------------- 出场 ----------------

    /// <summary>
    /// 出场：摆位置 → 画 → **先让窗口真的显示出来** → 再打洞 → 置顶 → 抢键盘。
    ///
    /// 顺序有讲究：窗口还没显示时调 <c>SetWindowRgn</c> 会时灵时不灵
    /// （实测四方位里 `left` 的按钮条偶发不可命中，其余方位却一直正常）。
    /// 区域要在窗口出现之后再设，这时窗口几何才是稳定的。
    /// </summary>
    public void Present()
    {
        MoveTo(_left, _top, _right - _left, _bottom - _top);
        Render();
        TrackRegionWith(Root);       // 尺寸定下来之后再补一次区域（见 TrackRegionWith 注释）
        Activate();
        ApplyHitRegion();
        BringToTop();

        RequestKeyboard();

        Diag.Log($"标尺覆盖层：{(_isBase ? "基准" : "目标")} 屏 #{_display.Index} " +
                 $"{_right - _left}x{_bottom - _top} @({_left},{_top}) scale={_scale:0.##} " +
                 $"元素 {Root.Children.Count} 个 区域 {RegionRects.Count} 块 " +
                 $"标尺={_marker:0.##} 区间[{ClampLo():0.##},{ClampHi():0.##}] " +
                 $"hwnd=0x{Handle.ToInt64():X}");
    }

    /// <summary>
    /// 把本窗口推到前景（键盘消息只发给前景窗口，这是 <see cref="OnRawKeyDown"/> 的前提）。
    ///
    /// ⚠ 系统只让"已是前景的进程 / 被前景进程启动的进程 / 刚收到输入的进程"抢前景。
    /// 被外部程序（比如验收脚本）拉起来时这一步通常拿不到，那时用户点一下标尺即可补上
    /// —— 见 <see cref="OnDragPressed"/>。
    /// </summary>
    private void RequestKeyboard()
    {
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            HookIslandKeyboard();     // 键盘消息送到岛子窗，钩子必须挂到那一层（幂等，早了就下次再试）
            TakeKeyboard();
            long fg = Win32Overlay.GetForegroundWindow().ToInt64();
            long me = Handle.ToInt64();
            // 焦点窗也打出来：键盘消息发给的是**焦点窗**（通常是岛子窗），
            // 挂钩子要挂到它身上 —— 只看到"本窗是前景"并不代表按键能到。
            long foc = Win32Overlay.GetFocus().ToInt64();
            Diag.Log($"请求前景：前景=0x{fg:X} 本窗=0x{me:X} 焦点=0x{foc:X}" +
                     (fg == me ? " ✓ 本窗为前景（键盘可送达）"
                               : " ⚠ 前景不是本窗（系统限制，点一下覆盖层即补上）"));
        });
    }

    // ---------------- 坐标与夹紧 ----------------

    /// <summary>本屏位于接缝的负方向一侧（垂直接缝的左方 / 水平接缝的上方）。</summary>
    private bool NegativeSide => _seam.Vertical
        ? _right <= _seamCoord + 1
        : _bottom <= _seamCoord + 1;

    /// <summary>接缝方向上的物理坐标 → 本窗口 DIP。</summary>
    private double AlongDip(double along) => (along - _alongOrigin) / _scale;

    private double ClampLo() => Math.Min(_overlapStart + 24, _overlapEnd - 24);
    private double ClampHi() => Math.Max(_overlapStart + 24, _overlapEnd - 24);
    private double Clamp(double v) => Math.Clamp(v, ClampLo(), ClampHi());

    /// <summary>
    /// (across, along) 区块 → 屏幕矩形。**坐标与宽高一起换轴**，这是唯一的换轴点。
    /// </summary>
    private (double x, double y, double w, double h) RectOf(
        double acrossStart, double alongStart, double acrossSize, double alongSize)
        => _seam.Vertical
            ? (acrossStart, alongStart, acrossSize, alongSize)
            : (alongStart, acrossStart, alongSize, acrossSize);

    /// <summary>摆一件美术并登记进打洞区域（登记是必需的：区域之外画不出来）。</summary>
    private void Place(FrameworkElement el,
                       double acrossStart, double alongStart, double acrossSize, double alongSize)
    {
        var (x, y, w, h) = RectOf(acrossStart, alongStart, acrossSize, alongSize);
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        el.Width = w;
        el.Height = h;
        Root.Children.Add(el);

        const double aa = 2;         // 给抗锯齿留边，不然圆角/斜边会被切成硬口
        RegionRects.Add(new Windows.Foundation.Rect(x - aa, y - aa, w + aa * 2, h + aa * 2));
    }

    // ---------------- 绘制 ----------------

    private void Render()
    {
        Root.Children.Clear();
        RegionRects.Clear();
        HitRects.Clear();

        bool negative = NegativeSide;
        var (a1, a2, near) = RulerGeometry.BarAcross(_seamCoord, _acrossOrigin, _scale, negative);
        double anchorAlong = AlongDip(_marker);

        // 标尺线：沿 across 从 near 伸进屏内 BarLength，about 接缝留 SeamGap
        AddLine(a1, anchorAlong, a2 - a1);

        // 三角标：尖端越过接缝 TipOverSeam
        AddTriangle(
            RulerGeometry.TriangleTipAcross(_seamCoord, _acrossOrigin, _scale, negative),
            near, anchorAlong);

        if (_isBase) AddBaseBar(a1, a2, anchorAlong, negative);
        else AddTargetBar(a1, a2, anchorAlong);

        // --fill N：铺一层 alpha=N 的纯黑画布底，做命中测试的 A/B。
        // 正常路径下 FillAlpha = 0 → 画布背景恒为 null（= 空白处穿透）。
        int fill = OverlayWindowBase.FillAlpha;
        Root.Background = fill > 0
            ? new SolidColorBrush(Color.FromArgb((byte)fill, 0, 0, 0))
            : null;

        ApplyHitRegion();           // 标尺挪了 → 区域跟着挪，否则按钮条在新位置点不到
    }

    private void AddLine(double acrossStart, double anchorAlong, double acrossSize)
    {
        var line = new Rectangle
        {
            Fill = AccentBrush,
            IsHitTestVisible = false,      // 线本身不吃鼠标（WPF 版同）
        };
        Place(line, acrossStart, anchorAlong - RulerGeometry.BarThickness / 2,
              acrossSize, RulerGeometry.BarThickness);
    }

    /// <summary>三角标：尖端在 <paramref name="tipAcross"/>，底边压在线的近端上。</summary>
    private void AddTriangle(double tipAcross, double nearAcross, double anchorAlong)
    {
        const double halfBase = 5;
        var tri = new Polygon { Fill = AccentBrush, IsHitTestVisible = false };
        tri.Points.Add(PointAt(tipAcross, anchorAlong));
        tri.Points.Add(PointAt(nearAcross, anchorAlong - halfBase));
        tri.Points.Add(PointAt(nearAcross, anchorAlong + halfBase));

        Place(tri, Math.Min(tipAcross, nearAcross), anchorAlong - halfBase,
              Math.Abs(tipAcross - nearAcross), halfBase * 2);
    }

    private Windows.Foundation.Point PointAt(double across, double along)
        => _seam.Vertical ? new Windows.Foundation.Point(across, along)
                          : new Windows.Foundation.Point(along, across);

    /// <summary>基准侧 <c>[≡][↺]</c>：骑线中点、沿 across 并排、拖动键靠接缝。</summary>
    private void AddBaseBar(double a1, double a2, double anchorAlong, bool negative)
    {
        int n = RulerGeometry.BaseBarButtonCount;
        double extent = RulerGeometry.ButtonBarExtent(n);
        double thickness = RulerGeometry.ButtonBarThickness;
        double midAcross = (a1 + a2) / 2;

        // 条沿 across 排、厚度在 along
        AddPanel(midAcross - extent / 2, anchorAlong - thickness / 2, extent, thickness);

        double offset = extent / 2 - RulerGeometry.ButtonBarPad - RulerGeometry.BarButtonSize / 2;
        double drag = midAcross - RulerGeometry.AwaySign(negative) * offset;
        double reset = midAcross + RulerGeometry.AwaySign(negative) * offset;

        AddButton(drag, anchorAlong, "\uE76F", Loc.RulerDragTip, DragBrush, dragHandle: true);
        AddButton(reset, anchorAlong, "\uE72C", Loc.RulerResetTip, ButtonBrush,
                  onTap: _ => ResetRequested?.Invoke());

        // 标签：跨 across 居中于条中心，沿 along 紧贴条外侧起始
        double tagAlongStart = anchorAlong + thickness / 2 + RulerGeometry.TagGapAlong;
        AddChip(Loc.RulerFixed, midAcross, tagAlongStart, 132, 22);
    }

    /// <summary>目标侧 <c>[▲][≡][▼]</c>：骑线中点、沿 along 三键成串、拖动键居中。</summary>
    private void AddTargetBar(double a1, double a2, double anchorAlong)
    {
        int n = RulerGeometry.TargetBarButtonCount;
        double extent = RulerGeometry.ButtonBarExtent(n);
        double thickness = RulerGeometry.ButtonBarThickness;
        double lineMid = (a1 + a2) / 2;

        // 条沿 along 排、厚度在 across
        AddPanel(lineMid - thickness / 2, anchorAlong - extent / 2, thickness, extent);

        double step = RulerGeometry.BarButtonSize + RulerGeometry.ButtonBarGap;
        string negGlyph = _seam.Vertical ? "\uE70E" : "\uE76B";   // 竖缝：上 / 横缝：左
        string posGlyph = _seam.Vertical ? "\uE70D" : "\uE76C";   // 竖缝：下 / 横缝：右

        // 微调键：Shift 按住走 10px（与 WPF 版 NudgeStep 同）
        AddButton(lineMid, anchorAlong - step, negGlyph, Loc.Px(-1), ButtonBrush,
                  onTap: mods => Nudge((mods.HasFlag(VirtualKeyModifiers.Shift) ? 10 : 1) * -1));
        AddButton(lineMid, anchorAlong, "\uE76F", Loc.RulerDragTip, DragBrush, dragHandle: true);
        AddButton(lineMid, anchorAlong + step, posGlyph, Loc.Px(1), ButtonBrush,
                  onTap: mods => Nudge(mods.HasFlag(VirtualKeyModifiers.Shift) ? 10 : 1));

        // 读数浮标：跨 across 居中于线中点，沿 along 紧贴条外侧起始。
        // 偏移用「条沿移动轴总长一半」而不是条厚一半 —— 拿错轴会把 ▼ 键整个盖住
        // （WPF 版 v1.6.0 首测实拍即中）。
        double readoutAlongStart = anchorAlong + extent / 2 + RulerGeometry.ReadoutGapAlong;
        AddChip(Loc.Px(PendingDelta), lineMid, readoutAlongStart, 64, 22);
    }

    /// <summary>
    /// 条容器：一处圆角面板。整块面板都算交互区（容器空白处也可拖），
    /// 所以它同时登记进"吃鼠标"的 <see cref="OverlayWindowBase.HitRects"/>。
    /// </summary>
    private void AddPanel(double acrossStart, double alongStart, double acrossSize, double alongSize)
    {
        var panel = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = PanelBrush,
            BorderThickness = new Thickness(1),
            BorderBrush = PanelEdgeBrush,
        };
        Place(panel, acrossStart, alongStart, acrossSize, alongSize);

        var (x, y, w, h) = RectOf(acrossStart, alongStart, acrossSize, alongSize);
        var rect = new Windows.Foundation.Rect(x, y, w, h);
        HitRects.Add(rect);                       // 验收脚本按它判断"该归本层"
        AttachDrag(panel);                        // 条上空白处也可拖
    }

    /// <summary>
    /// 条内一枚键。<paramref name="dragHandle"/> 为真就是拖动键（按下即开始拖），
    /// 否则是点按动作（微调 / 回到居中）。
    /// </summary>
    private void AddButton(double across, double along, string glyph, string name,
                           Brush background, bool dragHandle = false,
                           Action<VirtualKeyModifiers> onTap = null)
    {
        const double size = RulerGeometry.BarButtonSize;
        var btn = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = background,
            Child = new FontIcon { Glyph = glyph, FontSize = 13, Foreground = GlyphBrush },
        };
        AutomationProperties.SetName(btn, name);
        ToolTipService.SetToolTip(btn, name);
        Place(btn, across - size / 2, along - size / 2, size, size);

        if (dragHandle) AttachDrag(btn);
        else if (onTap != null)
        {
            // 用 PointerReleased 而不是 Tapped：只有前者带 KeyModifiers（Shift = 10px 步进）。
            // 动作要**排到队列里**再执行：Nudge 会立刻 Render()（整块画布重建），
            // 在事件派发中途把发送方元素摘掉是不安全的。
            btn.PointerReleased += (_, e) =>
            {
                var mods = e.KeyModifiers;
                e.Handled = true;
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                    .TryEnqueue(() => onTap(mods));
            };
        }
    }

    /// <summary>
    /// 标签 / 读数用的胶囊（纯显示，不参与命中）。
    ///
    /// 摆位规则与 WPF 版一致、也只有这一处：
    ///   · **跨 across 居中**（对齐条中心）
    ///   · **沿 along 紧贴条外侧起始**（起始边 = 传进来的 alongStart，不是中心）
    /// 早先把 along 也做了居中，胶囊就直接骑在按钮条上了。
    ///
    /// 尺寸按**屏幕**给（宽 × 高）：胶囊一律横排文字，宽永远沿屏幕 X；
    /// 于是竖缝（across = X）时"宽"是 across 尺寸，横缝（across = Y）时反而是 along 尺寸 ——
    /// 换轴在这里做掉，调用方只管写"多宽多高"。
    /// </summary>
    private void AddChip(string text, double acrossCenter, double alongStart, double screenW, double screenH)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = PanelBrush,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
            IsHitTestVisible = false,
        };

        double acrossSize = _seam.Vertical ? screenW : screenH;
        double alongSize = _seam.Vertical ? screenH : screenW;
        Place(chip, acrossCenter - acrossSize / 2, alongStart, acrossSize, alongSize);
    }

    // ---------------- 交互 ----------------

    // 键盘落点的说明见构造函数：Canvas / Border 都不是 Control、**不可聚焦**，
    // 而方向键微调与 Esc 退出必须要有一个真实的可聚焦控件接收按键。
    // 这枚 1×1 全透明 Button 不登记进 RegionRects（于是不占命中、也不影响打洞），
    // 纯粹当焦点容器用。

    private void AttachDrag(UIElement el)
    {
        el.PointerPressed += OnDragPressed;

        // 悬停就把焦点抢过来：两条标尺各自一个窗口，焦点只有一个，
        // 没抢到的那条会吃掉用户的第一次点击（见基类 FocusIsland 的注释）。
        // 指针一进入交互区就补上，点下去时本层必然已经"醒着"。
        //
        // 悬停就把焦点与区域都准备好，按下那一刻**什么都不改** ——
        // 实测按下时任何 Activate/SetFocus/SetWindowRgn 都会把 XAML 的指针捕获打断，
        // 表现为刚起手就"拖动结束"，100px 只跟到 20px。
        //
        // 悬停时**只抢焦点，不放宽区域**。
        //
        // ⚠ 放宽曾经也放在这里（想让"按下那一刻什么都不改"），代价是只要光标停在
        // 按钮条上，整块覆盖层就一直吃鼠标 —— 下面的窗口全点不到，这是真 bug。
        // 放宽只在**真的开始拖之后**才需要（见 OnDragPressed）：那时冻结区域 +
        // 尚未抓捕获，改区域打断不了什么，于是放在这里也不会出问题。
        el.PointerEntered += (_, _) =>
        {
            if (_dragging) return;
            TakeKeyboard();          // 焦点：没抢到的那条标尺会吃掉第一次点击
        };
    }

    /// <summary>指针位置在"沿接缝"那根轴上的 DIP 分量。</summary>
    private double AlongOf(Windows.Foundation.Point p) => _seam.Vertical ? p.Y : p.X;

    private void OnDragPressed(object sender, PointerRoutedEventArgs e)
    {
        // 两侧都可拖：基准侧动的是"锚点"（两屏标尺一起挪，不写系统），
        // 可动侧动的是对齐量（松手后写系统）。
        _dragging = true;
        _moveLogs = 0;
        _dragLastAlong = AlongOf(e.GetCurrentPoint(Root).Position);
        Diag.Log($"拖动起手：along={_dragLastAlong:0.##} 标尺={_marker:0.##}");

        // 起手只做两件事：冻结区域 + 捕获指针（捕获抓在 Root 上，见构造函数注释）。
        //
        // ⚠ 焦点不许在这里动 —— 实测 Activate / SetFocus 任何一样都会当场打断
        // XAML 的指针捕获：刚起手就"拖动结束"，100px 只跟到 20px。
        // 焦点已经在悬停时准备好了（见 AttachDrag）。
        // 顺序要紧：先放宽（这会 SetWindowRgn，但此时还没抓捕获，打断不了什么），
        // 再冻结、最后才抓捕获 —— 触摸路径没有"悬停"那一步，区域可能还是打洞状态，
        // 不先放宽的话手指一拖出按钮条就掉进洞里。
        SetWidened(true);
        RegionFrozen = true;        // 从此刻起再不许 SetWindowRgn（会打断刚刚抓到的捕获）
        Root.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnDragMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        // 增量映射：标尺 = 当前位置 + 本帧鼠标位移。
        //
        // 不能用"起点 + 总位移"的绝对映射：标尺会被夹紧在允许区间里，
        // 一旦拉过界，往回走的那段总位移会先被"死区"吃掉 ——
        // 实测拉到底再回拉 200px，标尺纹丝不动，用户以为坏了。
        // 增量映射下夹紧只会挡住越界方向，回拉立即跟手。
        double now = AlongOf(e.GetCurrentPoint(Root).Position);
        double delta = (now - _dragLastAlong) * _scale;
        _dragLastAlong = now;
        SetMarker(_marker + delta);

        // 只打前几次：拖动期间这一秒能来几十条，全打会把日志冲垮，
        // 而"到底有没有来 / 位移是多少"看开头几条就够了。
        if (_moveLogs < 5)
            Diag.Log($"拖动移动 #{++_moveLogs}：along={now:0.##} 位移={delta:0.##} 标尺={_marker:0.##}");

        e.Handled = true;
    }

    private void OnDragReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        //
        // 顺序要紧：先把拖动收尾，**再**放捕获。
        // ReleasePointerCapture 会**同步**触发 PointerCaptureLost —— 先放的话
        // 收尾是从 CaptureLost 那条路走的，于是日志里凭空多一条"捕获丢失（拖动中，异常）"，
        // 真正的异常反而被淹掉（踩过一次，白查了半天）。
        EndDrag(commit: true);
        _releasing = true;                        // 下面的 CaptureLost 是我们自己放的
        Root.ReleasePointerCapture(e.Pointer);     // 其实松手即自动释放，这里只是显式收干净
        _releasing = false;
        e.Handled = true;
    }

    private void OnDragCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_releasing) return;                   // 自己松手导致的，不是异常
        Diag.Log(_dragging
            ? $"指针捕获丢失（拖动中，异常）标尺={_marker:0.##}"
            : "指针捕获丢失（未在拖动，忽略）");
        EndDrag(commit: true);
    }

    private void EndDrag(bool commit)
    {
        if (!_dragging) return;
        _dragging = false;
        RegionFrozen = false;                   // 解冻：松手后区域要跟着标尺重设
        SetWidened(false);          // 一律收回：拖完了就该只剩按钮条吃鼠标
        _commitTimer.Stop();

        Diag.Log($"拖动结束：{(_isBase ? "锚点" : "标尺")}={_marker:0.##} 撞边界 {_clampHits} 次 " +
                 $"允许区间[{ClampLo():0.##},{ClampHi():0.##}]");
        _clampHits = 0;

        if (!commit) return;
        if (_isBase) AnchorSettled?.Invoke(_marker);   // 锚点移动不写系统
        else ScheduleCommit();                         // 松手即计算并应用

        // 拖动已经结束，这时补焦点才安全（起手时补会打断指针捕获，见 OnDragPressed）。
        // 触摸路径没有"悬停"那一步，靠这里兜住键盘。
        RequestKeyboard();
    }

    private void SetMarker(double value)
    {
        double clamped = Clamp(value);
        if (Math.Abs(clamped - value) > 0.5) _clampHits++;

        if (Math.Abs(clamped - _marker) < 0.01) return;
        _marker = clamped;
        Render();
        MarkerChanged?.Invoke(_marker);
    }

    private void ScheduleCommit()
    {
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    /// <summary>
    /// 键盘：Esc 退出对齐；沿接缝那根轴的方向键微调（Shift 按住走 10px）。
    ///
    /// 走的是窗口过程的原始消息（见基类 <see cref="OverlayWindowBase.OnRawKeyDown"/>），
    /// 不依赖 XAML 焦点 —— 前提是窗口在前景，见 <see cref="RequestKeyboard"/>。
    /// </summary>
    protected override bool OnRawKeyDown(int virtualKey, bool shiftDown)
    {
        if (virtualKey == (int)VirtualKey.Escape)
        {
            Diag.Log($"[{(_isBase ? "base" : "target")}] Esc → 请求退出对齐");
            CloseRequested?.Invoke();
            return true;
        }

        int step = shiftDown ? 10 : 1;
        bool vertical = _seam.Vertical;

        if (vertical && virtualKey == (int)VirtualKey.Up) { Nudge(-step); return true; }
        if (vertical && virtualKey == (int)VirtualKey.Down) { Nudge(step); return true; }
        if (!vertical && virtualKey == (int)VirtualKey.Left) { Nudge(-step); return true; }
        if (!vertical && virtualKey == (int)VirtualKey.Right) { Nudge(step); return true; }
        return false;
    }

    // ---------------- 验收探测 ----------------

    /// <summary>
    /// 把窗口矩形与本层全部命中矩形导成文本，供脚本做 <c>WindowFromPoint</c> 探测。
    /// 坐标一律换算到**屏幕物理像素**（脚本拿到就能直接探，不必再猜 DPI）。
    /// </summary>
    public string Probe(string label)
    {
        var sb = new StringBuilder();
        double s = DpiScale;
        sb.AppendLine($"  overlay={label} hwnd=0x{Handle.ToInt64():X} dpi={s:0.##} " +
                      $"region={RegionState} " +
                      $"window={_left},{_top},{_right},{_bottom} hits={HitRects.Count}");

        foreach (var r in HitRects)
        {
            int cx = _left + (int)Math.Round((r.X + r.Width / 2) * s);
            int cy = _top + (int)Math.Round((r.Y + r.Height / 2) * s);
            sb.AppendLine($"    hit={cx},{cy} dip=({r.X:0.#},{r.Y:0.#}) {r.Width:0.#}x{r.Height:0.#}");
        }

        // 窗口内、离所有美术都很远的点：验证"洞里穿透"。
        //
        // 取点有讲究：**不能简单取屏中心或几分之一屏** —— 横缝的标尺线是**竖直**的、
        // 长 180，正好穿过屏幕中线，于是"屏高 1/4 处"落在线上，探测会误判成"没穿透"
        // （实际是点取错了）。正确做法：从按钮条中心沿**沿轴**方向推远 400 DIP ——
        // 标签/读数最多占 116 DIP，400 之外一定什么也没有。
        double bcx = (_left + _right) / 2.0, bcy = (_top + _bottom) / 2.0;
        if (HitRects.Count > 0)
        {
            var r = HitRects[0];
            bcx = r.X + r.Width / 2;
            bcy = r.Y + r.Height / 2;
        }
        double bx = _seam.Vertical ? bcx : bcx + 400;
        double by = _seam.Vertical ? bcy + 400 : bcy;
        int mx = _left + (int)Math.Round(bx * s);
        int my = _top + (int)Math.Round(by * s);
        sb.AppendLine($"    blank={mx},{my} 说明=窗口内空白处（应穿透）");

        // 窗口外一点：确认探测本身能区分"不是本窗口"
        sb.AppendLine($"    outside={_left - 40},{_top - 40} 说明=窗口外");
        return sb.ToString();
    }

    // ---------------- 画刷 ----------------
    //
    // 覆盖层是浮在**任意**桌面内容之上的 HUD，所以它自持一套配色，
    // 不跟应用主题走：主题令牌在浅色下会把面板刷成浅底，压在任何壁纸/窗口上都读不清。
    // 唯一跟系统走的颜色是强调色（标尺线 / 三角）—— 那正是"用户挑的颜色"。
    // 窗口也已在 OverlayWindowBase 里声明 DWMWA_USE_IMMERSIVE_DARK_MODE，让 DWM 别加白。

    private static Color AccentColor
    {
        get
        {
            try
            {
                var s = new Windows.UI.ViewManagement.UISettings();
                return s.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            }
            catch { return Color.FromArgb(255, 0, 120, 212); }
        }
    }

    private static Brush PanelBrush => new SolidColorBrush(Color.FromArgb(0xF0, 0x20, 0x20, 0x24));
    private static Brush PanelEdgeBrush => new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
    private static Brush DragBrush => new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static Brush ButtonBrush => new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

    private static Brush AccentBrush => new SolidColorBrush(AccentColor);
    private static Brush GlyphBrush => new SolidColorBrush(Colors.White);
    private static Brush TextBrush => new SolidColorBrush(Color.FromArgb(255, 0xF2, 0xF2, 0xF2));
}
