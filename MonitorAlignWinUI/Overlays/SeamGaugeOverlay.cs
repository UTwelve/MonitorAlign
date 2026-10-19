using System;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MonitorAlign.Core;
using Windows.UI;

namespace MonitorAlign.Overlay;

/// <summary>
/// 「接缝标尺」覆盖层 —— 扩展功能页用它测量两块屏的**物理像素尺度比**。
///
/// ═══════════════ 它到底在测什么 ═══════════════
///
/// 接缝两端各生成**一对**标尺：
///   基准标尺（在 <see cref="Seam.Base"/> 那台屏上，固定不动）
///   可拖动标尺（在 <see cref="Seam.Target"/> 那台屏上，用户拖）
/// 一共两对。用户目视拖动副屏侧那两个，让每一对在**物理上**看起来连成一条直线。
///
/// 记：
///   L  = 两枚基准标尺的间距（基准屏的物理像素）
///   d₁ = 上/左那枚可拖动标尺相对标称位置的位移（副屏物理像素）
///   d₂ = 下/右那枚的位移
///   Δ  = d₂ − d₁
///   kA = 基准屏的 mm/物理像素，kB = 副屏的 mm/物理像素
///
/// 两对都对齐，等价于「同一段物理长度，在基准屏上占 L·kA 毫米，在副屏上占
/// (L+Δ)·kB 毫米」，于是
///       L·kA = (L + Δ)·kB      ⟹      kB / kA = L / (L + Δ)
///
/// 这个比值就是两块屏的**像素尺度比**。两块屏要想跨屏内容在物理上连续，
/// 副屏得跑一个"尺度退回去"的分辨率：
///       目标分辨率 = (W·ratio, H·ratio)      ratio = L / (L + Δ)
/// 保持宽高比 → 不变形；面板的缩放器把图像拉满整块屏 → 物理像素间距变成 kA。
///
/// ⚠ 这也解释了为什么不能只靠 EDID 的物理尺寸直接算：那玩意经常四舍五入到厘米
/// （本机报 1196×336 / 600×530），拿它算出来的目标和实测能差不少。
/// 标尺是把用户当量具 —— EDID 只用来给一个"理论值"做交叉验证（见 DevPage）。
///
/// ⚠ 本功能**只算不改**：不碰任何显示设置。
///
/// 实现上完全复用 RulerOverlay 趟出来的那套覆盖层规矩：
///   · 窗口开局固定不动，拖动只改内容元素的 TranslateTransform —— 绝不在拖动中
///     重建可视树 / 调 SetWindowRgn（那两条都会直接打断指针捕获）。
///   · 起手顺序：SetWidened(true) → RegionFrozen=true → CapturePointer。
///   · 松手先 EndDrag 再 ReleasePointerCapture，并用 _releasing 跳过自己放的那条
///     PointerCaptureLost（否则会记一次假"丢捕获"）。
/// </summary>
public sealed class SeamGaugeOverlay : OverlayWindowBase
{
    /// <summary>标尺线长度（逻辑像素）。</summary>
    private const double BarLengthDip = 180;

    /// <summary>标尺线粗细（逻辑像素）。</summary>
    private const double BarThicknessDip = 3;

    /// <summary>线端与接缝之间的留白（逻辑像素）—— 别贴死，否则两屏的线在接缝处糊成一团。</summary>
    private const double SeamGapDip = 6;

    /// <summary>标尺带垂直于线的厚度（逻辑像素。竖缝时是带子的高，横缝时是宽）。</summary>
    private const double BandAcrossDip = 130;

    /// <summary>可拖动标尺的最大行程（物理像素，两侧各这么多）。与 VM 共用同一个常量。</summary>
    private const double TravelPhys = SeamGaugeGeometry.Travel;

    private readonly Seam _seam;
    private readonly DisplayInfo _display;
    private readonly bool _vertical;
    private readonly bool _movable;
    private readonly double _scale;

    /// <summary>沿接缝方向的物理像素坐标（虚拟桌面）。</summary>
    private readonly double _nominal;

    /// <summary>本屏是否位于接缝的负方向一侧（左 / 上）。</summary>
    private readonly bool _negativeSide;

    // 拖动只动这个变形；窗口与可视树一律不重建。
    private readonly TranslateTransform _shift = new();
    private readonly TextBlock _readout = new();
    private readonly Canvas _root = new();
    private Grid _band;

    private bool _dragging;
    private double _dragStartAlong;     // 起手时"指针沿轴 − 当前位移"（物理px，窗口内）
    private double _offset;             // 相对标称位置的位移（物理px）
    private bool _releasing;

    /// <summary>位移变化（物理像素，正 = 沿轴正方向）。</summary>
    public event Action<SeamGaugeOverlay, double> OffsetChanged;

    /// <summary>当前位移（物理像素）。</summary>
    public double Offset => _offset;

    /// <summary>本标尺所在屏。</summary>
    public DisplayInfo Display => _display;

    /// <summary>true = 副屏那枚（可拖）；false = 基准屏那枚。</summary>
    public bool IsMovable => _movable;

    public SeamGaugeOverlay(Seam seam, DisplayInfo display, double nominalAlong, bool movable)
    {
        _seam = seam;
        _display = display;
        _vertical = seam.Vertical;
        _movable = movable;
        _scale = display.Scale <= 0 ? 1 : display.Scale;
        _nominal = nominalAlong;
        _negativeSide = ComputeNegativeSide();

        BuildContent();

        // 窗口沿轴铺满整个行程，across 只占标尺线那一小段。
        double acrossStart = AcrossStartOfSeam();
        int alongSpan = (int)Math.Round(TravelPhys * 2);
        int acrossSpan = (int)Math.Round(BarLengthDip * _scale);
        int left = _vertical ? (int)Math.Round(acrossStart) : (int)Math.Round(nominalAlong - TravelPhys);
        int top = _vertical ? (int)Math.Round(nominalAlong - TravelPhys) : (int)Math.Round(acrossStart);
        int w = _vertical ? acrossSpan : alongSpan;
        int h = _vertical ? alongSpan : acrossSpan;

        InitOverlay(noActivate: true, keyboardEnabled: false);       // 标尺永不用键盘，绝不抢焦点
        MoveTo(left, top, w, h);

        // WinUI 3 的 Window **创建出来是隐藏的**，不 Show 就只是个有句柄没 WS_VISIBLE 的窗
        // —— Rect 对、样式对、EnumWindows 也能枚举到，可屏幕上什么都没有，鼠标也不归它。
        // 泛光层是在 SetIntensity 里按需 Show 的，标尺没有那种"按需"，建完直接亮。
        // 放在 MoveTo 之后：先摆好位置再显示，避免在旧位置上闪一帧。
        AppWindow.Show();

        if (_movable)
        {
            SetPunchEnabled(true);           // 只有标尺带吃鼠标
            ApplyBandRegion();
        }
        else
        {
            SetClickThrough(true);           // 基准侧纯显示
        }

        TrackRegionWith(_root);
        BringToTop();
    }

    // ------------------------------------------------------------ 几何

    /// <summary>
    /// 本屏在接缝的哪一侧。用本屏的 across 中心与接缝坐标比较得出 ——
    /// 不写死方位（左右上下四种排布通用）。
    /// </summary>
    private bool ComputeNegativeSide()
    {
        double center = _vertical
            ? (_display.Rect.Left + _display.Rect.Right) / 2
            : (_display.Rect.Top + _display.Rect.Bottom) / 2;
        return center < _seam.SeamCoord;
    }

    /// <summary>标尺线在 across 轴上的起点（物理像素，虚拟桌面坐标）。</summary>
    private double AcrossStartOfSeam()
    {
        double gap = SeamGapDip * _scale;
        double len = BarLengthDip * _scale;
        return _negativeSide ? _seam.SeamCoord - gap - len : _seam.SeamCoord + gap;
    }

    /// <summary>窗口沿轴中心，在窗口内的 DIP 坐标（窗口沿轴长 = 2·Travel）。</summary>
    private double AlongCenterDip => TravelPhys / _scale;

    // ------------------------------------------------------------ 可视树

    private void BuildContent()
    {
        _root.Width = _vertical ? BarLengthDip : AlongCenterDip * 2;
        _root.Height = _vertical ? AlongCenterDip * 2 : BarLengthDip;
        _root.Background = null;             // 不设底色：底色会吃掉窗口透明

        var accent = _movable ? Color.FromArgb(255, 0x4C, 0xC2, 0xFF)      // 可拖：强调蓝
                              : Color.FromArgb(232, 0xE8, 0xEE, 0xF7);     // 基准：近白
        var brush = new SolidColorBrush(accent);

        // 标尺线：竖缝上是横线，横缝上是竖线。
        var bar = new Rectangle
        {
            Width = _vertical ? BarLengthDip : BarThicknessDip,
            Height = _vertical ? BarThicknessDip : BarLengthDip,
            Fill = brush,
        };

        // 指向接缝的小箭头，让"往哪边对齐"一眼可辨。
        var arrow = new TextBlock
        {
            Text = (_vertical ? (_negativeSide ? "\uE76C" : "\uE76B")     // 左右
                              : (_negativeSide ? "\uE70E" : "\uE70D")),   // 上下
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 9,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = _negativeSide ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };

        var barRow = new Grid
        {
            Width = _vertical ? BarLengthDip : BarThicknessDip,
            Height = _vertical ? BarThicknessDip + 6 : BarLengthDip,
        };
        barRow.Children.Add(bar);
        barRow.Children.Add(arrow);

        var label = new TextBlock
        {
            Text = _movable ? Loc.GaugeDragHint : Loc.GaugeBaseHint,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(220, 0xE8, 0xEE, 0xF7)),
            TextWrapping = TextWrapping.NoWrap,
        };

        _readout.FontSize = 11;
        _readout.FontWeight = FontWeights.SemiBold;
        _readout.Foreground = new SolidColorBrush(Color.FromArgb(255, 0x9F, 0xE0, 0xFF));
        _readout.TextWrapping = TextWrapping.NoWrap;
        _readout.Visibility = _movable ? Visibility.Visible : Visibility.Collapsed;
        _readout.Text = "0";

        var stack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        if (_vertical)
        {
            stack.Children.Add(barRow);
            stack.Children.Add(label);
            stack.Children.Add(_readout);
        }
        else
        {
            stack.Orientation = Orientation.Horizontal;
            stack.Children.Add(barRow);
            var col = new StackPanel
            {
                Spacing = 2,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            col.Children.Add(label);
            col.Children.Add(_readout);
            stack.Children.Add(col);
        }

        double bandAcross = _vertical ? BarLengthDip : BandAcrossDip;
        _band = new Grid
        {
            Width = _vertical ? bandAcross : bandAcross,
            Height = _vertical ? BandAcrossDip : BarLengthDip,
            Background = new SolidColorBrush(Colors.Transparent),   // 透明，但要吃鼠标
            RenderTransform = _shift,
        };
        _band.Children.Add(stack);
        if (_vertical)
        {
            Canvas.SetLeft(_band, 0);
            Canvas.SetTop(_band, AlongCenterDip - BandAcrossDip / 2);
        }
        else
        {
            Canvas.SetLeft(_band, AlongCenterDip - BandAcrossDip / 2);
            Canvas.SetTop(_band, 0);
        }
        _root.Children.Add(_band);

        if (_movable)
        {
            // 抓握提示（整条带子都能拖，这里只是让它看起来能拖）
            var grip = new TextBlock
            {
                Text = "≡",
                FontSize = 15,
                Foreground = new SolidColorBrush(Color.FromArgb(200, 0x9F, 0xE0, 0xFF)),
            };
            // 手柄必须在命中带内，并随标尺一起移动。
            grip.HorizontalAlignment = HorizontalAlignment.Right;
            grip.VerticalAlignment = VerticalAlignment.Bottom;
            grip.Margin = new Thickness(4);
            _band.Children.Add(grip);

            // ⚠ 四个事件**不能都挂带子上**，这是实拍踩出来的：
            //   起手时 `_root` 抢了指针捕获 —— 按下之后，指针事件全部派发给 `_root`
            //   并沿它的祖先链冒泡。带子是 `_root` 的**子级**，不在派发路径上，
            //   于是 PointerMoved / PointerReleased 一次都收不到：
            //   表现为"区域放宽了（按下确实到了）、位移却永远 0、松手也不收区域"。
            // 正确的分工与 RulerOverlay 一致：
            //   按下挂带子（只有带子范围才起拖），移动/松手/丢捕获挂根（捕获在哪就挂哪）。
            _band.PointerPressed += OnDragPressed;
            _root.PointerMoved += OnDragMoved;
            _root.PointerReleased += OnDragReleased;
            _root.PointerCaptureLost += OnDragCaptureLost;
        }

        Content = _root;
    }

    /// <summary>标尺带在窗口里的矩形（DIP）—— 命中区域就按它打洞。</summary>
    private void ApplyBandRegion()
    {
        RegionRects.Clear();
        double c = AlongCenterDip + _offset / _scale;
        if (_vertical)
            RegionRects.Add(new Windows.Foundation.Rect(0, c - BandAcrossDip / 2, BarLengthDip, BandAcrossDip));
        else
            RegionRects.Add(new Windows.Foundation.Rect(c - BandAcrossDip / 2, 0, BandAcrossDip, BarLengthDip));
        ApplyHitRegion();
    }

    // ------------------------------------------------------------ 拖动
    private double AlongOf(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_root).Position;
        return (_vertical ? p.Y : p.X) * _scale;      // → 物理像素（窗口内）
    }

    private void OnDragPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragStartAlong = AlongOf(e) - _offset;
        _dragging = true;

        // 起手顺序不能换（见类头注释）：放宽命中区域 → 冻住 → 抢捕获
        SetWidened(true);
        RegionFrozen = true;

        bool got = _root.CapturePointer(e.Pointer);
        if (!got) EndDrag();
        Diag.Log($"拖动起手：along={_dragStartAlong:0.##} 副屏={_display.Index} "
                 + $"一对={(_nominal < _seam.MidPoint ? "上/左" : "下/右")} 捕获={(got ? "成功" : "失败")}");
        e.Handled = true;
    }

    private void OnDragMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        SetOffset(AlongOf(e) - _dragStartAlong);
        e.Handled = true;
    }

    private void OnDragReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        // 高频移动可能被合并，松手坐标才是最终位置。
        SetOffset(AlongOf(e) - _dragStartAlong);
        e.Handled = true;
        _releasing = true;
        EndDrag();                                   // 先收拖动状态
        _root.ReleasePointerCapture(e.Pointer);      // 会同步触发 CaptureLost，靠 _releasing 跳过
        _releasing = false;
    }

    private void OnDragCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_releasing || !_dragging) return;
        Diag.Log("接缝标尺：拖动中丢了指针捕获");
        EndDrag();
    }

    private void EndDrag()
    {
        _dragging = false;
        RegionFrozen = false;
        SetWidened(false);
        ApplyBandRegion();                           // 区域收回标尺带
        Diag.Log($"拖动结束：offset={_offset:0.##} 副屏={_display.Index} → {RegionState}");
    }

    /// <summary>设置位移（物理像素），带行程限制与读数刷新。</summary>
    public void SetOffset(double physical)
    {
        if (!double.IsFinite(physical)) return;
        double v = Math.Clamp(physical, -TravelPhys, TravelPhys);
        if (Math.Abs(v - _offset) < 0.01) return;
        _offset = v;

        if (_vertical) _shift.Y = v / _scale;
        else _shift.X = v / _scale;

        _readout.Text = Loc.Fmt(Loc.GaugeOffsetFmt,
            (v > 0 ? "+" : v < 0 ? "−" : "") + Math.Abs(v).ToString("0"));
        if (!_dragging && _movable) ApplyBandRegion();
        OffsetChanged?.Invoke(this, v);
    }

    /// <summary>复位到标称位置。</summary>
    public void ResetOffset() => SetOffset(0);

    /// <summary>关闭并释放。</summary>
    public void Shutdown()
    {
        UnhookThreadKeyboard();
        Close();
    }
}
