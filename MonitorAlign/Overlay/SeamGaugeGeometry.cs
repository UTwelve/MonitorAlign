using System;
using MonitorAlign.Core;

namespace MonitorAlign.Overlay;

/// <summary>
/// 「接缝标尺 → 目标分辨率」的**纯几何**：不引用任何 UI 类型，只吃 <see cref="Seam"/> 与两个位移。
///
/// 单点化的理由与 <see cref="RulerGeometry"/> 一样：这套判定（标尺摆哪、比例怎么算）
/// 必须只有一份，否则 VM 显示的和覆盖层画的会各走各的。
///
/// ══════════════ 物理模型 ══════════════
///
/// 接缝两端各一对标尺：基准那枚在 <see cref="Seam.Base"/> 屏上（不动），
/// 可拖那枚在 <see cref="Seam.Target"/> 屏上。用户目视把每一对拖成一条直线。
///
///   L  = 两枚基准标尺的间距（基准屏物理像素）
///   d₁ = 上/左那枚可拖标尺相对标称位置的位移（副屏物理像素）
///   d₂ = 下/右那枚的位移
///   Δ  = d₂ − d₁
///   kA = 基准屏 mm/物理像素；kB = 副屏 mm/物理像素
///
/// 两对都对齐 ⟺ 同一段物理长度在基准屏上占 L·kA 毫米、在副屏上占 (L+Δ)·kB 毫米：
///       L·kA = (L + Δ)·kB      ⟹      kB / kA = L / (L + Δ) ≡ ratio
///
/// 要让两块屏的**物理像素间距一致**（跨屏内容在物理上连续），副屏应跑
///       目标分辨率 = (W·ratio, H·ratio)
/// 保持宽高比 → 不变形；面板缩放器把图像拉满整块屏，像素间距就变成 kA 了。
///
/// 若 Δ≈0 则 ratio≈1 —— 两边像素尺度本来就一致，不需要改分辨率。
/// </summary>
public static class SeamGaugeGeometry
{
    /// <summary>两对标尺相对接缝两端的内缩比例。</summary>
    public const double EndInsetRatio = 0.16;

    /// <summary>内缩的绝对下限（物理像素）。</summary>
    public const double EndInsetMin = 90;

    /// <summary>内缩的绝对上限（物理像素）。</summary>
    public const double EndInsetMax = 260;

    /// <summary>可拖动标尺的行程（物理像素，两侧各这么多）。</summary>
    public const double Travel = 420;

    /// <summary>内缩量：别把标尺怼到屏幕角落（那里可能被任务栏/边框挡住）。</summary>
    public static double Inset(int overlapLength)
    {
        double half = Math.Max(overlapLength / 2.0 - 24, 1);
        double upper = Math.Min(EndInsetMax, half);
        return Math.Clamp(overlapLength * EndInsetRatio, Math.Min(EndInsetMin, upper), upper);
    }

    /// <summary>
    /// 两对标尺的标称位置（沿接缝方向，虚拟桌面物理像素）与其间距 L。
    /// first = 上/左那一对，second = 下/右那一对。
    /// </summary>
    public static (double First, double Second, double Length) Placement(Seam s)
    {
        double inset = Inset(s.OverlapEnd - s.OverlapStart);
        double a = s.OverlapStart + inset;
        double b = s.OverlapEnd - inset;
        if (b <= a)                       // 接缝太短：退化到中点，仍然是"一对"
        {
            double m = s.MidPoint;
            a = m - 1;
            b = m + 1;
        }
        return (a, b, b - a);
    }

    /// <summary>
    /// 由两个位移算出结果。
    /// 单位：d₁/d₂ 是**副屏物理像素**；返回的 W/H 是目标分辨率。
    /// </summary>
    public static (double Ratio, int Width, int Height) Solve(Seam s, double d1, double d2)
    {
        var (_, _, L) = Placement(s);
        double delta = d2 - d1;
        double denom = L + delta;

        // 分母过小/为负 = 用户把两枚标尺拖反了，这时比值没有意义，退回 1。
        double ratio = denom > L * 0.2 ? L / denom : 1.0;

        int w = Math.Max(1, (int)Math.Round(s.Target.PelsWidth * ratio));
        int h = Math.Max(1, (int)Math.Round(s.Target.PelsHeight * ratio));
        return (ratio, w, h);
    }

    /// <summary>
    /// 沿接缝方向该用哪根轴的 mm/物理像素：
    /// 竖直接缝（左右并排）看**高度**是否匹配；水平接缝（上下叠放）看**宽度**。
    /// </summary>
    public static double PitchMm(DisplayInfo d, bool vertical)
    {
        if (vertical) return d.PelsHeight > 0 && d.PhysicalHeightMm > 0
            ? d.PhysicalHeightMm / (double)d.PelsHeight : 0;
        return d.PelsWidth > 0 && d.PhysicalWidthMm > 0
            ? d.PhysicalWidthMm / (double)d.PelsWidth : 0;
    }

    /// <summary>
    /// 用 EDID 的物理尺寸算出的**理论**比值 —— 只用来和实测交叉验证。
    /// EDID 常被四舍五入到厘米，所以它只是个参考，不是判据。
    /// </summary>
    public static double TheoreticalRatio(Seam s)
    {
        double ka = PitchMm(s.Base, s.Vertical);
        double kb = PitchMm(s.Target, s.Vertical);
        return ka > 0 && kb > 0 ? kb / ka : 1.0;
    }

    /// <summary>接缝方向轴上的总长度（用于界面文案）。</summary>
    public static int OverlapLength(Seam s) => s.OverlapEnd - s.OverlapStart;
}
