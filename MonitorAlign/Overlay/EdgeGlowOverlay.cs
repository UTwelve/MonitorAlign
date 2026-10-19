using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MonitorAlign.Overlay;

/// <summary>
/// 一条接缝的"边缘泛光"覆盖层：把接缝段当成一根**矩形光源**——
/// 一条贴近白色的亮芯光带贴着接缝，向两侧按指数衰减地"照射"出彩色光晕。
/// 靠近光源处发白（真实光源的亮度感），光晕颜色取主题强调色或该接缝的自定义色。
///
/// 与对齐标尺同一套窗口手法：WindowStyle.None + AllowsTransparency + Background=null
/// —— null 背景不参与命中测试，窗口整体点击穿透，只做视觉效果（置顶不挡操作）。
/// 亮度用窗口 Opacity 表达（layered 窗口改透明度很便宜），位置尺寸只在创建时定一次。
/// </summary>
public sealed class EdgeGlowOverlay : Window
{
    /// <summary>亮芯光带的厚度（逻辑像素）。</summary>
    public const double CoreThickness = 6;

    private readonly bool _vertical;
    private readonly double _overlapStart, _seamCoord, _distance;
    private double _brightness = 0.6;

    /// <summary>0..1 的辉光颜色；近光源处会向白色混合出"发白"的亮芯。</summary>
    public EdgeGlowOverlay(Core.Seam seam, Color color, double distance)
    {
        _vertical = seam.Vertical;
        _overlapStart = seam.OverlapStart;
        _seamCoord = seam.SeamCoord;
        _distance = Math.Clamp(distance, 24, 320);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = null;                 // 关键：不吃鼠标，点击穿透
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        Opacity = 0;

        double length = Math.Max(seam.OverlapEnd - seam.OverlapStart, 8);
        BuildContent(color, length);

        if (_vertical)
        {
            Width = _distance * 2;
            Height = length;
            Left = _seamCoord - _distance;
            Top = _overlapStart;
        }
        else
        {
            Width = length;
            Height = _distance * 2;
            Left = _overlapStart;
            Top = _seamCoord - _distance;
        }
    }

    private void BuildContent(Color color, double length)
    {
        // 光晕：垂直于接缝方向的指数衰减渐变（近似 1/(1+k·d) 的观感），
        // 中心最亮、两侧迅速弱下去——"从光源里照出来"而不是"一块雾"。
        var halo = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = _vertical ? new Point(1, 0) : new Point(0, 1),
        };
        double h = 0.55;                       // 光晕峰值透明度
        (double off, double a)[] stops =
        {
            (0.00, 0.00), (0.15, 0.03), (0.30, 0.12), (0.40, 0.30),
            (0.50, 1.00),
            (0.60, 0.30), (0.70, 0.12), (0.85, 0.03), (1.00, 0.00),
        };
        foreach (var (off, a) in stops)
            halo.GradientStops.Add(new GradientStop(
                Color.FromArgb((byte)Math.Round(255 * h * a), color.R, color.G, color.B), off));

        // 沿接缝方向两端渐隐（软圆头，不出硬边）
        var along = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = _vertical ? new Point(0, 1) : new Point(1, 0),
        };
        along.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        along.GradientStops.Add(new GradientStop(Colors.White, 0.06));
        along.GradientStops.Add(new GradientStop(Colors.White, 0.94));
        along.GradientStops.Add(new GradientStop(Colors.Transparent, 1));

        var haloRect = new Rectangle { Fill = halo, OpacityMask = along };

        // 亮芯（光源本体）：与颜色混一点白 → 近看发白，远看带色。
        // 用同一条 OpacityMask 收两端。
        var coreColor = Blend(color, Colors.White, 0.82);
        var core = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, coreColor.R, coreColor.G, coreColor.B)),
            CornerRadius = new CornerRadius(CoreThickness / 2),
            OpacityMask = along.Clone(),
            Width = _vertical ? CoreThickness : length,
            Height = _vertical ? length : CoreThickness,
            HorizontalAlignment = _vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
            VerticalAlignment = _vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center,
        };

        Content = new Grid { Children = { haloRect, core } };
    }

    /// <summary>全局亮度（0..1）。改变它不需要重建，下一次 SetIntensity 生效。</summary>
    public void SetBrightness(double brightness) => _brightness = Math.Clamp(brightness, 0.05, 1);

    /// <summary>0..1 接近度 → 窗口透明度 = 接近度 × 亮度；接近 0 直接隐藏省合成开销。</summary>
    public void SetIntensity(double intensity)
    {
        double o = Math.Clamp(intensity, 0, 1) * _brightness;
        Opacity = o;
        bool want = o > 0.02;
        if (want && !IsVisible) Show();
        else if (!want && IsVisible) Hide();
    }

    private static Color Blend(Color a, Color b, double towardB)
    {
        double t = Math.Clamp(towardB, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }
}
