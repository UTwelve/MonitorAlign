using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Interop;
using System.Windows.Threading;
using MonitorAlign.Core;

namespace MonitorAlign.Overlay;

/// <summary>
/// 单台显示器上的覆盖层。
///
/// 设计要点：
/// 1) Background = null（不是 Transparent）——WPF 中 null 背景不参与命中测试，
///    因此空白区域鼠标直接穿透到下面的桌面，只有画出来的元素能接收事件。
/// 2) 参考物是一条「沿法线方向伸向接缝」的线段，沿接缝方向移动：
///    左右并排（垂直接缝）→ 水平线，上下移动；上下叠放（水平接缝）→ 垂直线，左右移动。
///    各锚点（线端、三角尖、基准标签）全部由 <see cref="RulerGeometry"/> 一处算出，
///    这里只负责把 (along, across) 映射成 (x, y) 再画 —— 四方位共用一套判定。
/// 3) 位置变化先通知 MarkerChanged（实时读数），再用 320ms 防抖触发 CommitRequested，
///    避免连续微调时反复触发显示模式变更（每次都会闪屏）。
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly DisplayInfo _display;
    private readonly Seam _seam;
    private readonly bool _isBase;
    private readonly double _scale;
    private readonly Canvas _canvas = new();

    private double _marker;          // 参考物位置（逻辑像素，沿接缝方向）
    private bool _dragging;

    // 会话内固定的几何快照。
    // 应用补偿会改变目标屏的逻辑矩形，若继续引用实时坐标，接缝重叠区间与 MidPoint
    // 会跟着漂移，导致多次微调产生累积误差。因此对齐会话期间一律使用进入时的快照。
    private readonly int _dispLeft, _dispTop, _dispRight, _dispBottom;
    private readonly int _seamCoord, _midPoint, _overlapStart, _overlapEnd;
    private double _dragStartMarker;
    private Point _dragStartPoint;
    private Point _lastDragPoint;
    private int _clampHits;

    /// <summary>上次成功提交时的标尺位置（会话逻辑像素）。提交只算"自这里以来的增量"。</summary>
    private double _lastCommitted;
    private readonly DispatcherTimer _commitTimer;

    /// <summary>参考物位置变化（沿接缝方向的逻辑像素）。</summary>
    public event Action<double> MarkerChanged;
    /// <summary>防抖后请求把当前位置写入系统。</summary>
    public event Action<double> CommitRequested;
    /// <summary>基准侧拖动/键盘移动锚点结束（不写系统，仅用于持久化与联动）。</summary>
    public event Action<double> AnchorSettled;
    /// <summary>基准侧点了"回到居中"。</summary>
    public event Action ResetRequested;
    /// <summary>用户按 Esc 请求退出对齐。</summary>
    public event Action CloseRequested;

    public OverlayWindow(DisplayInfo display, Seam seam, bool isBase)
    {
        _display = display;
        _seam = seam;
        _isBase = isBase;
        _scale = display.Scale <= 0 ? 1 : display.Scale;
        _marker = seam.MidPoint;

        _dispLeft = display.Left;
        _dispTop = display.Top;
        _dispRight = display.Right;
        _dispBottom = display.Bottom;
        _seamCoord = seam.SeamCoord;
        _midPoint = seam.MidPoint;
        _overlapStart = seam.OverlapStart;
        _overlapEnd = seam.OverlapEnd;
        _lastCommitted = _midPoint;          // 会话开始时没有任何待提交的增量

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = null;                 // 关键：空白区不吃鼠标
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = true;

        // 虚拟桌面物理像素 → WPF 的 DIP
        Left = display.Left / _scale;
        Top = display.Top / _scale;
        Width = display.Width / _scale;
        Height = display.Height / _scale;

        Content = _canvas;

        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _commitTimer.Tick += (_, _) => { _commitTimer.Stop(); CommitRequested?.Invoke(_marker); };

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            Render();
            Focus();
            Diag.Log($"覆盖层就绪：{(isBase ? "基准" : "目标")} 屏 #{display.Index} " +
                     $"{display.Width}x{display.Height} scale={_scale:0.##} " +
                     $"窗口 {Width:0}x{Height:0} @({Left:0},{Top:0}) 元素 {_canvas.Children.Count} 个 " +
                     $"参考物={_marker:0} 区间[{_overlapStart},{_overlapEnd}] " +
                     $"hwnd=0x{new WindowInteropHelper(this).Handle:X}");
        };

        // 拖动与释放挂在窗口上，配合鼠标捕获，拖出元素外也不会丢
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
    }

    /// <summary>当前参考物位置（逻辑像素）。</summary>
    public double Marker => _marker;

    /// <summary>
    /// 自上次成功提交以来的增量（逻辑像素）—— 下一次提交就应用这么多。
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
        Render();                             // 读数浮标归零，与"两条线已对齐"的视觉一致
    }

    /// <summary>
    /// 撤销到启动前之后调用：排列回去了，但标尺还停在拖过的位置，
    /// 必须把零点也重置，否则下一次提交会漏算这段已撤销的量。
    /// v1.3.0 起零点不是接缝中点而是**当前锚点**（基准标尺的位置）：
    /// 锚点被用户挪过之后，"无需补偿"的位置就是锚点本身。
    /// </summary>
    public void ResetCommitBaseline(double anchor)
    {
        _lastCommitted = anchor;
        Render();
    }

    /// <summary>外部（微调按钮/键盘）推动参考物，单位逻辑像素。</summary>
    public void Nudge(int delta)
    {
        SetMarker(_marker + delta);
        if (_isBase) AnchorSettled?.Invoke(_marker);   // 基准侧动的是锚点，不写系统
        else ScheduleCommit();
    }

    /// <summary>
    /// 外部把参考物直接设到指定位置（不触发任何事件）。
    /// 用于：基准侧拖动时主窗口把锚点同步到另一侧、恢复存档的锚点。
    /// </summary>
    public void SetMarkerSilent(double value)
    {
        double lo = Math.Min(_overlapStart + 24, _overlapEnd - 24);
        double hi = Math.Max(_overlapStart + 24, _overlapEnd - 24);
        _marker = Math.Clamp(value, lo, hi);
        Render();
    }

    /// <summary>
    /// 恢复存档的锚点：参考物与提交零点一起设到该位置（待提交增量归零 —
    /// 对齐结果上次已经写进系统，不能重算）。
    /// </summary>
    public void AdoptAnchor(double value)
    {
        SetMarkerSilent(value);
        _lastCommitted = _marker;
    }

    /// <summary>
    /// 基准侧移动锚点时同步平移本侧：参考物位置与提交零点一起挪，
    /// 待提交增量（PendingDelta）保持不变 —— 移动标尺不等于修改对齐结果。
    /// </summary>
    public void ShiftAnchor(double deltaLogical)
    {
        _marker += deltaLogical;
        _lastCommitted += deltaLogical;
        Render();
    }

    private void SetMarker(double value)
    {
        double lo = _overlapStart + 24;
        double hi = _overlapEnd - 24;
        double lo2 = Math.Min(lo, hi), hi2 = Math.Max(lo, hi);

        double clamped = Math.Clamp(value, lo2, hi2);
        if (Math.Abs(clamped - value) > 0.5) _clampHits++;   // 撞到边界的次数

        _marker = clamped;
        Render();
        MarkerChanged?.Invoke(_marker);
    }

    private void ScheduleCommit()
    {
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    // ---------- 坐标换算 ----------

    /// <summary>沿接缝方向的逻辑坐标 → 窗口内 DIP（用会话快照坐标）。</summary>
    private double AlongToDip(double logical) => (logical - (_seam.Vertical ? _dispTop : _dispLeft)) / _scale;

    /// <summary>法线方向的逻辑坐标 → 窗口内 DIP（用会话快照坐标）。</summary>
    private double AcrossToDip(double logical) => (logical - (_seam.Vertical ? _dispLeft : _dispTop)) / _scale;

    /// <summary>本屏是否位于接缝的「负方向」一侧（垂直接缝的左方 / 水平接缝的上方）。</summary>
    private bool IsNegativeSide => _seam.Vertical
        ? _dispRight <= _seamCoord + 1
        : _dispBottom <= _seamCoord + 1;

    // ---------- 绘制 ----------

    private void Render()
    {
        _canvas.Children.Clear();

        var markerBrush = Res("MaMarkerBrush");
        var outlineBrush = Res("MaMarkerOutlineBrush");
        var panelBrush = Res("MaMarkerPanelBrush");
        var textBrush = Res("MaMarkerTextBrush");

        double along = AlongToDip(_marker);          // 沿接缝方向的位置

        // 线在法线方向的区间。near = **靠接缝的那一端**，三角尖与基准标签都锚在它上面，
        // 所以"哪一端靠接缝"全局只有一处判定（RulerGeometry），两套画法不可能再各算各的。
        var (across1, across2, near) = RulerGeometry.BarAcross(
            _seamCoord, _seam.Vertical ? _display.Left : _display.Top, _scale, IsNegativeSide);

        double thick = RulerGeometry.BarThickness / _scale;

        // 线（描边 + 主体，保证在任意壁纸上可见）
        AddBar(across1, along, across2 - across1, thick, outlineBrush, 2.5 / _scale);
        AddBar(across1, along, across2 - across1, thick, markerBrush, 0);

        if (_seam.Vertical)
        {
            DrawVerticalSeamExtras(across1, across2, near, along, panelBrush, textBrush, markerBrush, outlineBrush);
        }
        else
        {
            DrawHorizontalSeamExtras(across1, across2, near, along, panelBrush, textBrush, markerBrush, outlineBrush);
        }
    }

    /// <summary>基准侧的标签：基准不可拖，全靠这枚标签说明它是什么。</summary>
    private static Border MakeBaseTag(Brush panelBrush, Brush textBrush) => new()
    {
        Background = panelBrush,
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(7, 3, 7, 3),
        Child = new TextBlock { Text = Loc.RulerFixed, FontSize = 12, Foreground = textBrush }
    };

    private void DrawVerticalSeamExtras(double x1, double x2, double near, double y,
        Brush panelBrush, Brush textBrush, Brush markerBrush, Brush outlineBrush)
    {
        // 零位三角标，指向接缝
        double tipX = RulerGeometry.TriangleTipAcross(_seamCoord, _display.Left, _scale, IsNegativeSide);
        var tri = new Polygon
        {
            Fill = markerBrush,
            Stroke = outlineBrush,
            StrokeThickness = 1,
            Points = new PointCollection
            {
                new Point(tipX, y),
                new Point(IsNegativeSide ? tipX - 9 / _scale : tipX + 9 / _scale, y - 5 / _scale),
                new Point(IsNegativeSide ? tipX - 9 / _scale : tipX + 9 / _scale, y + 5 / _scale),
            }
        };
        _canvas.Children.Add(tri);

        double mid = (x1 + x2) / 2;

        // 抓取区先铺在最底层：整条线（含 bar 底下的部分）都可发起拖动
        AddGrab(Math.Min(x1, x2), y, Math.Abs(x2 - x1), verticalSeam: true);

        if (_isBase)
        {
            // [≡][↺] 沿线并排：拖动键靠接缝一侧（四方位镜像），整条 bar 骑在线中点
            var bar = BuildBaseBar(panelBrush, markerBrush, textBrush, verticalSeam: true);
            AddCentered(bar, mid, y);

            // 基准标签：条正下方（沿轴外侧）——不骑线、不与按钮重叠，
            // 旧布局"标签贴线端 + 手柄在线中点"必然互相遮挡，这就是根因
            var tag = MakeBaseTag(panelBrush, textBrush);
            tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tag, mid - tag.DesiredSize.Width / 2);
            Canvas.SetTop(tag, y + (RulerGeometry.ButtonBarThickness / 2 + RulerGeometry.TagGapAlong) / _scale);
            _canvas.Children.Add(tag);
            return;
        }

        // 游标刻度：沿 Y 方向，每 12px 一条，中心加长加粗
        double tickBaseX = IsNegativeSide ? x1 + 4 : x2 - 4;
        for (int i = -3; i <= 3; i++)
        {
            double ty = y + i * 12 / _scale;
            bool center = i == 0;
            double len = center ? 22 : 12;
            double lx = IsNegativeSide ? tickBaseX : tickBaseX - len / _scale;
            AddTick(lx, ty - 0.75, len / _scale, 1.5, center ? 1 : 0.7, outlineBrush, markerBrush);
        }

        // [▲ / ≡ / ▼] 三键成串：拖动居中、微调在上下，整条 bar 骑在线中点
        var targetBar = BuildTargetBar(panelBrush, markerBrush, textBrush, verticalSeam: true);
        AddCentered(targetBar, mid, y);

        // 读数浮标：排在条沿轴外侧。偏移要用条沿移动轴的总长（±44），
        // 不是条厚（±16）——拿错轴会把 ▼ 键整个盖住（v1.6.0 首测实拍即中）
        var readout = MakeReadout(PendingDelta, panelBrush, textBrush, markerBrush);
        readout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(readout, mid - readout.DesiredSize.Width / 2);
        Canvas.SetTop(readout, y + (RulerGeometry.ButtonBarExtent(RulerGeometry.TargetBarButtonCount) / 2
                                    + RulerGeometry.ReadoutGapAlong) / _scale);
        _canvas.Children.Add(readout);
    }

    private void DrawHorizontalSeamExtras(double y1, double y2, double near, double x,
        Brush panelBrush, Brush textBrush, Brush markerBrush, Brush outlineBrush)
    {
        double tipY = RulerGeometry.TriangleTipAcross(_seamCoord, _display.Top, _scale, IsNegativeSide);
        var tri = new Polygon
        {
            Fill = markerBrush,
            Stroke = outlineBrush,
            StrokeThickness = 1,
            Points = new PointCollection
            {
                new Point(x, tipY),
                new Point(x - 5 / _scale, IsNegativeSide ? tipY - 9 / _scale : tipY + 9 / _scale),
                new Point(x + 5 / _scale, IsNegativeSide ? tipY - 9 / _scale : tipY + 9 / _scale),
            }
        };
        _canvas.Children.Add(tri);

        double mid = (y1 + y2) / 2;

        // 抓取区先铺在最底层
        AddGrab(x, Math.Min(y1, y2), Math.Abs(y2 - y1), verticalSeam: false);

        if (_isBase)
        {
            // [≡][↺] 上下成串（across 轴 = Y）：拖动键靠接缝一侧，bar 骑在线中点
            var bar = BuildBaseBar(panelBrush, markerBrush, textBrush, verticalSeam: false);
            AddCentered(bar, x, mid);

            // 基准标签：条沿轴外侧（右侧），居中
            var tag = MakeBaseTag(panelBrush, textBrush);
            tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tag, x + (RulerGeometry.ButtonBarThickness / 2 + RulerGeometry.TagGapAlong) / _scale);
            Canvas.SetTop(tag, mid - tag.DesiredSize.Height / 2);
            _canvas.Children.Add(tag);
            return;
        }

        // 游标刻度：沿 X 方向
        double tickBaseY = IsNegativeSide ? y1 + 4 : y2 - 4;
        for (int i = -3; i <= 3; i++)
        {
            double tx = x + i * 12 / _scale;
            bool center = i == 0;
            double len = center ? 22 : 12;
            double ty = IsNegativeSide ? tickBaseY : tickBaseY - len / _scale;
            AddTick(tx - 0.75, ty, 1.5, len / _scale, center ? 1 : 0.7, outlineBrush, markerBrush);
        }

        // [◀ / ≡ / ▶] 三键成串（沿移动轴 = X）：拖动居中，bar 骑在线中点
        var targetBar = BuildTargetBar(panelBrush, markerBrush, textBrush, verticalSeam: false);
        AddCentered(targetBar, x, mid);

        // 读数浮标：排在条沿轴外侧（右），偏移用条沿移动轴总长（±44），不是条厚（±16）
        var readout = MakeReadout(PendingDelta, panelBrush, textBrush, markerBrush);
        readout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(readout, x + (RulerGeometry.ButtonBarExtent(RulerGeometry.TargetBarButtonCount) / 2
                                     + RulerGeometry.ReadoutGapAlong) / _scale);
        Canvas.SetTop(readout, mid - readout.DesiredSize.Height / 2);
        _canvas.Children.Add(readout);
    }

    // ---------- 按钮条（v1.6.0：按钮组合成一段 bar） ----------

    /// <summary>基准侧按钮条：[≡ 拖动][↺ 重置]。拖动键永远靠接缝一侧（四方位镜像）。</summary>
    private Border BuildBaseBar(Brush panelBrush, Brush markerBrush, Brush textBrush, bool verticalSeam)
    {
        // 基准条沿线（across 轴）排列：竖直接缝 → 水平条；水平接缝 → 垂直条
        bool stackAlong = !verticalSeam;
        var bar = BuildButtonBar(stackAlong, panelBrush, markerBrush);

        var drag = MakeBarDragButton(textBrush, verticalSeam);
        var reset = MakeBarButton("↺", Loc.RulerResetTip, panelBrush, textBrush,
            verticalSeam ? Cursors.SizeNS : Cursors.SizeWE);
        reset.Click += (_, _) => ResetRequested?.Invoke();

        // 正方向侧（接缝在低 across 端）→ 拖动排前（靠接缝）；负方向侧镜像
        bool dragFirst = !IsNegativeSide;
        AppendButton(bar, dragFirst ? drag : reset, stackAlong);
        AppendButton(bar, dragFirst ? reset : drag, stackAlong);
        return bar;
    }

    /// <summary>目标侧按钮条：[▲/≡/▼] 或 [◀/≡/▶]。拖动居中，微调键在两侧。</summary>
    private Border BuildTargetBar(Brush panelBrush, Brush markerBrush, Brush textBrush, bool verticalSeam)
    {
        // 目标条沿移动（along 轴）排列：竖直接缝 → 垂直条；水平接缝 → 水平条
        bool stackAlong = verticalSeam;
        var bar = BuildButtonBar(stackAlong, panelBrush, markerBrush);

        var drag = MakeBarDragButton(textBrush, verticalSeam);
        var minus = MakeBarButton(verticalSeam ? "▲" : "◀", Loc.RulerDragTip, panelBrush, textBrush, Cursors.Hand);
        var plus = MakeBarButton(verticalSeam ? "▼" : "▶", Loc.RulerDragTip, panelBrush, textBrush, Cursors.Hand);
        minus.Click += (_, _) => NudgeStep(-1);
        plus.Click += (_, _) => NudgeStep(1);

        AppendButton(bar, minus, stackAlong);
        AppendButton(bar, drag, stackAlong);
        AppendButton(bar, plus, stackAlong);
        return bar;
    }

    /// <summary>按钮条容器：圆角面板 + StackPanel；容器空白处也可发起拖动。</summary>
    private Border BuildButtonBar(bool stackAlong, Brush panelBrush, Brush markerBrush)
    {
        var bar = new Border
        {
            Background = panelBrush,
            BorderBrush = markerBrush,
            BorderThickness = new Thickness(1 / _scale),
            CornerRadius = new CornerRadius(7 / _scale),
            Padding = new Thickness(RulerGeometry.ButtonBarPad / _scale),
            Child = new StackPanel
            {
                Orientation = stackAlong ? Orientation.Vertical : Orientation.Horizontal
            }
        };
        bar.MouseLeftButtonDown += (_, e) => BeginDrag(e);   // 按钮之间的缝隙 / 边缘也能拖
        return bar;
    }

    private void AppendButton(Border bar, FrameworkElement button, bool stackAlong)
    {
        var panel = (StackPanel)bar.Child;
        double gap = RulerGeometry.ButtonBarGap / _scale;
        if (panel.Children.Count > 0)
            button.Margin = stackAlong
                ? new Thickness(0, gap, 0, 0)
                : new Thickness(gap, 0, 0, 0);
        panel.Children.Add(button);
    }

    /// <summary>条内拖动键：主操作，用强调色实底与其他键区分。</summary>
    private Border MakeBarDragButton(Brush textBrush, bool verticalSeam)
    {
        var b = new Border
        {
            Width = RulerGeometry.BarButtonSize / _scale,
            Height = RulerGeometry.BarButtonSize / _scale,
            CornerRadius = new CornerRadius(4 / _scale),
            Background = Res("MaMarkerBrush"),
            Cursor = verticalSeam ? Cursors.SizeNS : Cursors.SizeWE,
            ToolTip = Loc.RulerDragTip,
            Child = new TextBlock
            {
                Text = "≡", FontSize = 13, Foreground = textBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        b.MouseLeftButtonDown += (_, e) => BeginDrag(e);
        return b;
    }

    /// <summary>条内普通键（微调 / 重置）：长按连发，复用覆盖层按钮样式。</summary>
    private System.Windows.Controls.Primitives.RepeatButton MakeBarButton(
        string glyph, string tip, Brush bg, Brush fg, Cursor cursor)
    {
        var btn = new System.Windows.Controls.Primitives.RepeatButton
        {
            Content = glyph,
            Width = RulerGeometry.BarButtonSize / _scale,
            Height = RulerGeometry.BarButtonSize / _scale,
            Foreground = fg,
            Background = bg,
            Cursor = cursor,
            ToolTip = tip
        };
        if (Application.Current.TryFindResource("OverlayRepeatButton") is Style s)
            btn.Style = s;
        return btn;
    }

    /// <summary>量完尺寸后把元素中心对到 (cx, cy) 再入画布。</summary>
    private void AddCentered(UIElement el, double cx, double cy)
    {
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(el, cx - el.DesiredSize.Width / 2);
        Canvas.SetTop(el, cy - el.DesiredSize.Height / 2);
        _canvas.Children.Add(el);
    }

    /// <summary>抓取区（透明但可命中）：覆盖整条线及其附近。</summary>
    private void AddGrab(double h, double v, double len, bool verticalSeam)
    {
        var grab = new Rectangle
        {
            Fill = Brushes.Transparent,
            Cursor = verticalSeam ? Cursors.SizeNS : Cursors.SizeWE
        };
        if (verticalSeam)
        {
            grab.Width = len;
            grab.Height = 34 / _scale;
            Canvas.SetLeft(grab, h);
            Canvas.SetTop(grab, v - 17 / _scale);
        }
        else
        {
            grab.Width = 34 / _scale;
            grab.Height = len;
            Canvas.SetLeft(grab, h - 17 / _scale);
            Canvas.SetTop(grab, v);
        }
        grab.MouseLeftButtonDown += (_, e) => BeginDrag(e);
        _canvas.Children.Add(grab);
    }

    /// <summary>读数浮标：上行是偏移量，下行是「对齐标尺」这个名字。</summary>
    private static Border MakeReadout(int delta, Brush panelBrush, Brush textBrush, Brush markerBrush)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"{delta:+0;-#} px",
            FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = textBrush,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI")
        });
        panel.Children.Add(new TextBlock
        {
            Text = Loc.RulerMovable,
            FontSize = 10, Foreground = textBrush, Opacity = 0.66, Margin = new Thickness(0, 1, 0, 0)
        });

        return new Border
        {
            Background = panelBrush,
            BorderBrush = markerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Child = panel
        };
    }

    /// <summary>
    /// 画一个刻度：先用描边色垫一圈底，再压主体色。
    /// 只画主体色的话，浅蓝刻度落在同样偏蓝的壁纸上就几乎看不见了 —— 覆盖层必须假定
    /// 背景是任意壁纸，所以每个元素都自带对比描边。
    /// </summary>
    private void AddTick(double left, double top, double w, double h, double opacity,
        Brush outline, Brush core)
    {
        const double e = 0.8;

        var under = new Rectangle
        {
            Width = w + e * 2, Height = h + e * 2,
            Fill = outline, Opacity = opacity, RadiusX = 1, RadiusY = 1
        };
        Canvas.SetLeft(under, left - e);
        Canvas.SetTop(under, top - e);
        _canvas.Children.Add(under);

        var bar = new Rectangle
        {
            Width = w, Height = h,
            Fill = core, Opacity = opacity, RadiusX = 1, RadiusY = 1
        };
        Canvas.SetLeft(bar, left);
        Canvas.SetTop(bar, top);
        _canvas.Children.Add(bar);
    }

    /// <summary>
    /// 画一条沿法线方向的条：（左右并排时为水平条，上下叠放时为垂直条）。
    /// a1 是该轴上的起点，along 是另一轴上的位置。
    /// </summary>
    private void AddBar(double a1, double along, double len, double thick, Brush brush, double inflate)
    {
        Rectangle r;
        if (_seam.Vertical)
        {
            r = new Rectangle { Width = len, Height = thick + inflate * 2, Fill = brush, RadiusX = 1.5, RadiusY = 1.5 };
            Canvas.SetLeft(r, a1 - inflate);
            Canvas.SetTop(r, along - (thick + inflate * 2) / 2);
        }
        else
        {
            r = new Rectangle { Width = thick + inflate * 2, Height = len, Fill = brush, RadiusX = 1.5, RadiusY = 1.5 };
            Canvas.SetLeft(r, along - (thick + inflate * 2) / 2);
            Canvas.SetTop(r, a1 - inflate);
        }
        _canvas.Children.Add(r);
    }

    private static Brush Res(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.DodgerBlue;

    // ---------- 交互 ----------

    private void NudgeStep(int direction)
    {
        int step = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ? 10 : 1;
        Nudge(direction * step);
    }

    private void BeginDrag(MouseButtonEventArgs e)
    {
        // 两侧都可拖：基准侧动的是"锚点"（两屏标尺一起挪，不写系统），
        // 可动侧动的是对齐量（松手后写系统）。
        _dragging = true;
        _dragStartMarker = _marker;
        _dragStartPoint = e.GetPosition(this);
        _lastDragPoint = _dragStartPoint;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        // 增量映射：标尺 = 当前位置 + 本帧鼠标位移。
        //
        // 不能用"起点 + 总位移"的绝对映射：标尺会被夹紧在允许区间里，
        // 一旦拉过界，往回走的那段总位移会先被"死区"吃掉 ——
        // 实测拉到底再回拉 200px，标尺纹丝不动，用户以为坏了。
        // 增量映射下夹紧只会挡住越界方向，回拉立即跟手。
        var p = e.GetPosition(this);                     // DIP，相对窗口
        double lastAlong = _seam.Vertical ? _lastDragPoint.Y : _lastDragPoint.X;
        double nowAlong = _seam.Vertical ? p.Y : p.X;
        double deltaLogical = (nowAlong - lastAlong) * _scale;

        _lastDragPoint = p;
        SetMarker(_marker + deltaLogical);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        // 撞边界的次数很关键：拖动是"起点 + 位移"的绝对映射，一旦被夹紧，
        // 鼠标往回走的那段位移会被吃掉，表现为"拉到底再拉回来就跟不上了"。
        Diag.Log($"拖动结束：{( _isBase ? "锚点" : "标尺" )}={_marker:0.##} 撞边界 {_clampHits} 次 " +
                 $"允许区间[{Math.Min(_overlapStart + 24, _overlapEnd - 24):0.##}," +
                 $"{Math.Max(_overlapStart + 24, _overlapEnd - 24):0.##}]");
        _clampHits = 0;

        if (_isBase) AnchorSettled?.Invoke(_marker);   // 锚点移动不写系统
        else ScheduleCommit();                         // 松手即计算并应用
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseRequested?.Invoke();
            e.Handled = true;
            return;
        }

        int step = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ? 10 : 1;
        if (_seam.Vertical)
        {
            if (e.Key == Key.Up) { Nudge(-step); e.Handled = true; }
            else if (e.Key == Key.Down) { Nudge(step); e.Handled = true; }
        }
        else
        {
            if (e.Key == Key.Left) { Nudge(-step); e.Handled = true; }
            else if (e.Key == Key.Right) { Nudge(step); e.Handled = true; }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _commitTimer.Stop();
        base.OnClosed(e);
    }
}
