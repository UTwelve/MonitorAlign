using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MonitorAlign.Core;
using Windows.UI;

namespace MonitorAlign.Overlay;

/// <summary>
/// 一条接缝的「边缘泛光」覆盖层：把接缝段当成一根**矩形光源**，
/// 沿垂直于接缝的方向按物理衰减照射出光晕。
///
/// ══════════════════════════ 为什么长这样 ══════════════════════════
///
/// 【1】真 ADD 混合做不到，但可以**数学等价**地逼近。
///   DWM 合成顶层窗口只有 source-over（预乘 alpha），Windows 没有任何公开 API
///   能改窗口的混合模式：UpdateLayeredWindow 只认 AC_SRC_OVER，DWM 没有 blend 属性，
///   DComp 的混合状态也管不到与桌面的合成。唯一能读"窗口背后内容"的原语是
///   Compositor.CreateHostBackdropBrush()，而 WinAppSDK 2.5.1 的
///   Microsoft.UI.Composition 里**没有这个成员**（已在投影程序集核对）。
///   抓屏再自己加法合成会带来黄框、延迟和每帧几十 MB 的拷贝 —— 不划算。
///
///   但是在**线性光域**里，source-over 取
///       alpha = min(I, 1)
///   时，对暗背景有  bg + alpha·(光 − bg) ≡ bg + I·光  ——与 ADD 逐像素相同。
///   实测（设计/拟合发光参数.py）：暗背景平均偏差 0.0007、最大 0.0025。
///   亮背景上 source-over 最多只能到"不透明"，这是**不可约**的差异，
///   所以颜色不能太深（否则亮背景会被压暗成污渍）——
///   见下面的 TailWhiteness。
///
/// 【2】光形是**物理衰减**，不是拍脑袋的渐变。
///       I(x) = CoreGain·core(x) + 1/(1+(x/NearK)^NearPower)
///                                  + BloomGain/(1+(x/BloomK)^BloomPower)
///   近场是线光源的 1/d² 型衰减，外加一层衰减更慢的 Bloom（大气散射）。
///   衰减一律以**像素**为自变量：改「光照距离」只改"照到多远"，不改光的形状。
///
/// 【3】XAML/DWM 在 **sRGB 空间**混合，物理模型在**线性光域**。
///   直接把 alpha 交给 XAML，尾部会被压暗约 5 倍
///   （实测 a=0.056 时屏上只有 0.056，物理值应为 0.268）——这就是原来
///   "光晕又短又弱"的根因。修正：停靠点 alpha 取 <c>alpha^(1/2.2)</c>，
///   在纯黑背景上这一步是**精确**的，深色背景近似精确。
///   （修正后与真 ADD 的实拍差：见 设计/_lab_dark.png 第①②③栏。）
///
/// 【4】亮度 / 接近度乘在**强度**上，不乘在 alpha 上。
///   乘 alpha 的后果是芯部永远到不了不透明，白热变成一层灰雾。
///   亮度改的是 I，所以改亮度必须重建渐变（很便宜：只改停靠点，不重建窗口）；
///   接近度是 30fps 变的，走根元素 Opacity（并按 1/2.2 做伽马补偿）。
///
/// 【5】过曝泛白：越亮越白（真实高温光源的去饱和）。
///   芯部恒为全白，尾部保留 <see cref="TailWhiteness"/> 的白量 ——
///   这条滑条就是「加性强度」：白度越高越像 ADD（亮背景上不被压暗），
///   越低颜色越浓（但白底上会压暗约 15%）。
///
/// ⚠ 所有常数由 <c>设计/发光实验室.html</c>（WebGL，含真 ADD 基准栏）+
///   <c>设计/拟合发光参数.py</c> 拟合得出。**改之前先回实验室看效果**，
///   别在这里拍脑袋调数字。
///
/// 命中策略与标尺层不同：泛光是柔和渐变，必须整窗可见
/// （区域打洞会把渐变裁掉 —— 区域同时裁剪绘制），所以走「整窗穿透」。
/// </summary>
public sealed class GlowOverlay : OverlayWindowBase
{
    // ======================= 光形常数（实验室导出） =======================

    /// <summary>亮芯宽度（物理像素）。</summary>
    private const double CoreWidthPx = 5;

    /// <summary>亮芯增益（相对近场峰值）。</summary>
    private const double CoreGain = 1.2;

    /// <summary>近场特征长度（px）与衰减指数。</summary>
    private const double NearK = 20;
    private const double NearPower = 2.0;

    /// <summary>Bloom（大气散射）特征长度、增益与指数 —— 衰减比近场慢。</summary>
    private const double BloomK = 110;
    private const double BloomGain = 0.08;
    private const double BloomPower = 2.5;

    /// <summary>总曝光（线性光域）。</summary>
    private const double Exposure = 0.9;

    /// <summary>过曝泛白的陡度。</summary>
    private const double Whitening = 0.5;

    /// <summary>sRGB 编码指数。停靠点 alpha 要取 a^(1/2.2) 来补偿 XAML 的 sRGB 混合。</summary>
    private const double SrgbGamma = 2.2;

    /// <summary>渐变每侧的停靠点数（按 alpha 均匀分布 —— 在变化最快处自动加密）。</summary>
    private const int StopsPerSide = 64;

    /// <summary>光在 D 处平滑收口（D 的 75% 起开始收）。</summary>
    private const double TaperStart = 0.75;

    /// <summary>沿接缝方向两端的渐隐比例。</summary>
    private const double FadeSpan = 0.06;

    // =====================================================================

    private readonly bool _vertical;
    private readonly double _distance;
    private readonly Color _color;
    private readonly Grid _root = new();
    private readonly LinearGradientBrush _halo;
    private readonly GradientStop[] _stops;
    private readonly List<Border> _cells = new();

    /// <summary>尾部保留多少白（0=纯彩色，1=全白）。「加性强度」滑条。</summary>
    private double _tailW = 0.25;

    /// <summary>亮度 0.05..1。改它要重建渐变（只改停靠点，不重建窗口）。</summary>
    private double _brightness = 0.6;

    private bool _shown;

    public GlowOverlay(Seam seam, Color color, double distance, double tailWhiteness)
    {
        _vertical = seam.Vertical;
        _color = color;
        _tailW = Math.Clamp(tailWhiteness, 0, 1);

        double length = Math.Max(seam.OverlapEnd - seam.OverlapStart, 8);
        double dist = Math.Max(distance, 1);

        // 渐变沿"垂直于接缝"的方向铺满整窗：中心 = 接缝，两侧各 dist。
        _halo = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = _vertical ? new Windows.Foundation.Point(1, 0)
                                 : new Windows.Foundation.Point(0, 1),
        };
        // 停靠点数组一次分配到位，之后只改 Offset/Color ——
        // 亮度滑条拖动时每帧都重建，反复 new 129 个 DependencyObject 太浪费。
        _stops = new GradientStop[StopsPerSide * 2 + 1];
        for (int i = 0; i < _stops.Length; i++)
        {
            _stops[i] = new GradientStop { Offset = 0, Color = Colors.Transparent };
            _halo.GradientStops.Add(_stops[i]);
        }
        _distance = dist;

        BuildContent(length, dist);

        // WinUI 3 的 AppWindow 尺寸/位置一律是**物理像素**，
        // 而 Seam 的坐标本来就是物理像素 —— 于是这里不需要像 WPF 版那样换算 DIP。
        int w = (int)Math.Round(_vertical ? dist * 2 : length);
        int h = (int)Math.Round(_vertical ? length : dist * 2);
        int left = (int)Math.Round(_vertical ? seam.SeamCoord - dist : seam.OverlapStart);
        int top = (int)Math.Round(_vertical ? seam.OverlapStart : seam.SeamCoord - dist);

        Content = _root;
        InitOverlay(noActivate: true, keyboardEnabled: false);     // 泛光永不吃鼠标、永不抢焦点
        MoveTo(left, top, w, h);
        SetClickThrough(true);             // 整窗穿透（不加区域，渐变才不会被裁）
        RebuildGradient();
        BringToTop();
    }

    // ---------------------------------------------------------------- 光形

    /// <summary>线性光域的光强 I(x)，x = 离接缝的垂直距离（物理像素）。</summary>
    private double Intensity(double x)
    {
        if (x >= _distance) return 0;

        double core = 1 - SmoothStep(CoreWidthPx * 0.35, CoreWidthPx * 1.15, x);
        double near = 1 / (1 + Math.Pow(x / NearK, NearPower));
        double bloom = BloomGain / (1 + Math.Pow(x / BloomK, BloomPower));
        double taper = 1 - SmoothStep(_distance * TaperStart, _distance, x);

        return (CoreGain * core + near + bloom) * taper * Exposure * _brightness;
    }

    /// <summary>该处的 (sRGB 混合用 alpha, 颜色)：见类头【1】【3】【5】。</summary>
    private (double A, Color C) Sample(double x)
    {
        double I = Intensity(x);
        double a = Math.Min(I, 1.0);
        double aEff = Math.Pow(Math.Max(a, 0), 1.0 / SrgbGamma);   // 【3】补偿 sRGB 混合

        double sat = Math.Clamp(Math.Pow(Math.Min(I, 6.0), 1.25) * Whitening, 0, 1);
        double w = _tailW + (1 - _tailW) * sat;                    // 【5】芯部→1，尾部→_tailW
        return (aEff, Blend(_color, Colors.White, w));
    }

    /// <summary>
    /// 重建渐变停靠点。
    ///
    /// 停靠点按 **alpha 均匀**分布（而不是按位置均匀）：在 alpha 变化最快的地方
    /// （也就是亮芯两侧）自动加密，分段线性插值的误差最小。
    /// 按位置均匀的话，D=300 时最近的两个停靠点相距 4.7px，亮芯只有 5px 宽 ——
    /// 会被一个线段糊掉，芯部直接散架。
    /// </summary>
    private void RebuildGradient()
    {
        double a0 = Sample(0).A;
        if (a0 <= 0.0005)
        {
            foreach (var s in _stops) s.Color = Colors.Transparent;
            return;
        }

        for (int k = 0; k <= StopsPerSide; k++)
        {
            double target = a0 * (1.0 - (double)k / StopsPerSide);
            double x = k == 0 ? 0 : Invert(target, a0);
            var (a, c) = Sample(x);
            double off = 0.5 * x / _distance;          // 0（接缝）..0.5（窗口边）

            SetStop(StopsPerSide + k, 0.5 + off, a, c);   // 右半
            SetStop(StopsPerSide - k, 0.5 - off, a, c);   // 左半（镜像）
        }
    }

    private void SetStop(int index, double offset, double alpha, Color c)
    {
        var s = _stops[index];
        s.Offset = offset;
        s.Color = Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);
    }

    /// <summary>反解 alpha(x) = target 的 x（alpha 关于 x 单调下降，二分即可）。</summary>
    private double Invert(double target, double a0)
    {
        double lo = 0, hi = _distance;
        for (int i = 0; i < 32; i++)
        {
            double mid = (lo + hi) / 2;
            if (Sample(mid).A > target) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    // ---------------------------------------------------------------- 可视树

    private void BuildContent(double length, double dist)
    {
        // ⚠ 两端的渐隐是**切片**实现的，不是 OpacityMask ——
        // WinUI 3 的 UIElement 没有 OpacityMask（WPF/UWP 有，WinUI 摘掉了），
        // 而"横向渐变 × 纵向渐隐"是两个轴的乘积，一支 LinearGradientBrush 给不出。
        //
        // 切片不能均匀撒：渐隐只占两端各 6%，均匀撒 40 片的话爬坡区只有 2 片，
        // 台阶约 50% 透明度，肉眼就是一条条带子。这里把 2/3 的切片压进两端的爬坡区。
        var fracs = SliceFractions();
        for (int i = 0; i < fracs.Length - 1; i++)
        {
            double frac = fracs[i + 1] - fracs[i];
            double fade = Fade((fracs[i] + fracs[i + 1]) / 2);

            // 端部渐隐也乘在**强度**上，所以也要做同样的伽马补偿。
            var cell = new Border
            {
                Opacity = Math.Pow(fade, 1.0 / SrgbGamma),
                Background = _halo,
            };

            // Star 行/列由布局引擎精确铺满：相邻片共用边界，不留缝也不重叠
            // （重叠会让半透明的两层叠出一条深线）。
            if (_vertical)
            {
                _root.RowDefinitions.Add(new RowDefinition
                { Height = new GridLength(frac, GridUnitType.Star) });
                Grid.SetRow(cell, i);
            }
            else
            {
                _root.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(frac, GridUnitType.Star) });
                Grid.SetColumn(cell, i);
            }
            _root.Children.Add(cell);
            _cells.Add(cell);
        }
    }

    /// <summary>切片边界（0..1）：两端爬坡区各 16 片、中段 8 片。</summary>
    private static double[] SliceFractions()
    {
        const int ramp = 16, mid = 8;
        var list = new List<double>();
        for (int i = 0; i <= ramp; i++) list.Add(FadeSpan * i / ramp);
        for (int i = 1; i <= mid; i++) list.Add(FadeSpan + (1 - 2 * FadeSpan) * i / mid);
        for (int i = 1; i <= ramp; i++) list.Add(1 - FadeSpan + FadeSpan * i / ramp);
        return list.ToArray();
    }

    /// <summary>smoothstep 渐隐：比线性更贴"光自然消散"的观感，端点一阶导为 0 不出硬拐角。</summary>
    private static double Fade(double t)
    {
        if (t <= 0 || t >= 1) return 0;
        if (t <= FadeSpan) return Smooth(t / FadeSpan);
        if (t >= 1 - FadeSpan) return Smooth((1 - t) / FadeSpan);
        return 1;
    }

    private static double Smooth(double s) => s * s * (3 - 2 * s);

    private static double SmoothStep(double a, double b, double x)
    {
        if (b <= a) return x < a ? 0 : 1;
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static Color Blend(Color a, Color b, double towardB)
    {
        double t = Math.Clamp(towardB, 0, 1);
        return Color.FromArgb(255,
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    // ---------------------------------------------------------------- 外部接口

    /// <summary>尾部白度（「加性强度」）。改它要重建渐变。</summary>
    public void SetTailWhiteness(double w)
    {
        double v = Math.Clamp(w, 0, 1);
        if (Math.Abs(v - _tailW) < 1e-6) return;
        _tailW = v;
        RebuildGradient();
    }

    /// <summary>全局亮度（0..1）。改它要重建渐变 —— 因为亮度乘的是**强度**而不是 alpha。</summary>
    public void SetBrightness(double brightness)
    {
        double v = Math.Clamp(brightness, 0.05, 1);
        if (Math.Abs(v - _brightness) < 1e-6) return;
        _brightness = v;
        RebuildGradient();
    }

    /// <summary>
    /// 0..1 接近度 → 根元素透明度。
    ///
    /// 这里是唯一一处"乘在 alpha 上"的地方 —— 因为接近度是 30fps 变的，
    /// 不可能每帧重建渐变。物理上它就是"光在变暗"，所以补一次 1/2.2：
    /// 光强降到 10% 时屏上是 0.1^(1/2.2) ≈ 35%，不是 10%。
    /// 接近 0 直接隐藏，省合成开销。
    /// </summary>
    public void SetIntensity(double intensity)
    {
        double t = Math.Clamp(intensity, 0, 1);
        _root.Opacity = Math.Pow(t, 1.0 / SrgbGamma);

        bool want = t > 0.02;
        if (want == _shown) return;
        _shown = want;

        // WinUI 3 的窗口显隐走 AppWindow（Window 没有 Show/Hide）
        if (want) AppWindow.Show();
        else AppWindow.Hide();
    }

    /// <summary>本层覆盖的屏幕矩形（物理像素），验收脚本据此取点。</summary>
    public (int Left, int Top, int Width, int Height) ScreenRect
    {
        get
        {
            var p = AppWindow.Position;
            var s = AppWindow.Size;
            return (p.X, p.Y, s.Width, s.Height);
        }
    }

    /// <summary>关闭并释放。</summary>
    public void Shutdown() => Close();

    /// <summary>
    /// 验收探测：把本层 hwnd 与矩形导成文本。
    /// 泛光层是整窗穿透的，所以脚本在这里取到的**任何点都不能归本层** ——
    /// 这条判据与标尺层相反，正好互为对照。
    /// </summary>
    public string Probe(string label)
    {
        var r = ScreenRect;
        return $"  glow={label} hwnd=0x{Handle.ToInt64():X} region={RegionState} " +
               $"window={r.Left},{r.Top},{r.Left + r.Width},{r.Top + r.Height}\n" +
               $"    blank={r.Left + r.Width / 2},{r.Top + r.Height / 2} 说明=泛光正中（应穿透）\n" +
               $"    edge={r.Left + 2},{r.Top + 2} 说明=泛光左上角（应穿透）\n";
    }

    /// <summary>
    /// 光形剖面的自证：按当前参数打印若干距离上的 alpha（供 --glowprofile 用）。
    /// 用来对着 设计/拟合发光参数.py 的输出核账。
    /// </summary>
    public string Profile(int samples = 12)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i <= samples; i++)
        {
            double x = _distance * i / samples;
            double I = Intensity(x);
            var (a, c) = Sample(x);
            sb.AppendLine($"    x={x,7:0.0}px  I={I,6:0.3}  alpha={a,6:0.3}  " +
                          $"#{c.R:X2}{c.G:X2}{c.B:X2}");
        }
        return sb.ToString();
    }
}
