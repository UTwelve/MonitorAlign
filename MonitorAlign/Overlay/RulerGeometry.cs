namespace MonitorAlign.Overlay;

/// <summary>
/// 「对齐标尺」的纯几何 —— 不引用任何 WPF 元素，只吃逻辑像素 + 缩放，吐窗口内 DIP。
///
/// 为什么要单拎出来：原来竖直接缝与水平接缝各写了一套画法，而"哪一端靠接缝、
/// 标签往哪边让"是两套各自算的，于是同一个 bug 有两份：
///   副屏在左（基准侧在接缝正方向）时标签贴着接缝，
///   副屏在右（基准侧在接缝负方向）时标签跑到线的最远端，离接缝 180px。
/// 四方位里错了三个。单点化之后只有一份判定，不可能再各走各的；
/// 顺带让 --geomcheck 能对四个方位直接断言，不必靠肉眼。
///
/// v1.6.0 起按钮改为「组合 bar」：
///   基准侧 [≡][↺] 沿线（across 轴）并排，拖动键永远靠接缝一侧；
///   目标侧 [▲/≡/▼] 沿移动（along 轴）三键成串，拖动居中；
///   基准标签与读数浮标都挪到条沿轴外侧 —— 标签不再骑线（旧布局里
///   宽约 140px 的标签与线中点的手柄必然重叠，这就是"标尺文字被遮挡"的根因）。
///
/// 局部坐标轴（以"本屏"为参照）：
///   across = 垂直于接缝的方向（左右并排 → 屏幕 X；上下叠放 → 屏幕 Y）
///   along  = 沿接缝方向      （左右并排 → 屏幕 Y；上下叠放 → 屏幕 X）
/// 除标了 Logical 的参数外，所有数值都是窗口左上角为原点的 DIP。
/// </summary>
public static class RulerGeometry
{
    /// <summary>标尺线长度（逻辑像素，不随缩放变化，保证四方位长度一致）。</summary>
    public const double BarLength = 180;

    /// <summary>线宽（逻辑像素）。</summary>
    public const double BarThickness = 3;

    /// <summary>线端与接缝之间的留白。不能贴死，否则两屏的线在接缝处糊成一团，看不出是不是一条直线。</summary>
    public const double SeamGap = 6;

    /// <summary>三角箭头尖端越过接缝的量（逻辑像素）。超出本窗口的部分会被裁掉，视觉上就是"箭头尖顶到屏幕边"。</summary>
    public const double TipOverSeam = 2;

    // ---------- 按钮条（v1.6.0） ----------

    /// <summary>按钮条上单枚按钮的边长（逻辑像素）。</summary>
    public const double BarButtonSize = 26;

    /// <summary>条内相邻按钮的间距（逻辑像素）。</summary>
    public const double ButtonBarGap = 2;

    /// <summary>条容器的内边距（逻辑像素，一圈）。</summary>
    public const double ButtonBarPad = 3;

    /// <summary>基准标签与按钮条的沿轴间距（逻辑像素）。</summary>
    public const double TagGapAlong = 8;

    /// <summary>读数浮标与按钮条的沿轴间距（逻辑像素）。</summary>
    public const double ReadoutGapAlong = 6;

    /// <summary>按钮条沿排列方向的总长（逻辑像素）。n = 按钮数（基准 2 / 目标 3）。</summary>
    public static double ButtonBarExtent(int buttons)
        => buttons * BarButtonSize + (buttons - 1) * ButtonBarGap + ButtonBarPad * 2;

    /// <summary>按钮条垂直于排列方向的厚度（逻辑像素）。</summary>
    public static double ButtonBarThickness => BarButtonSize + ButtonBarPad * 2;

    /// <summary>基准侧按钮数。</summary>
    public const int BaseBarButtonCount = 2;

    /// <summary>目标侧按钮数。</summary>
    public const int TargetBarButtonCount = 3;

    /// <summary>
    /// "远离接缝"的单位方向（across 轴上的符号）：负方向侧为 -1，正方向侧为 +1。
    /// 基准条里重置键往这个方向排（拖动键就自然落在靠接缝一侧）。
    /// </summary>
    public static double AwaySign(bool negativeSide) => negativeSide ? -1 : 1;

    /// <summary>
    /// 线在 across 轴上的区间。negativeSide = 本屏位于接缝的负方向一侧
    /// （垂直接缝的左方 / 水平接缝的上方）。
    /// a1 &lt; a2 恒成立（与画图代码的约定一致），near 是靠近接缝的那一端。
    /// </summary>
    public static (double a1, double a2, double near) BarAcross(
        double seamCoordLogical, double displayOriginAcrossLogical, double scale, bool negativeSide)
    {
        scale = Norm(scale);

        double near = (negativeSide
            ? seamCoordLogical - SeamGap - displayOriginAcrossLogical
            : seamCoordLogical + SeamGap - displayOriginAcrossLogical) / scale;
        double far = negativeSide ? near - BarLength / scale : near + BarLength / scale;

        return (Math.Min(near, far), Math.Max(near, far), near);
    }

    /// <summary>三角箭头尖端在 across 轴上的位置。永远指向接缝，两侧对称。</summary>
    public static double TriangleTipAcross(
        double seamCoordLogical, double displayOriginAcrossLogical, double scale, bool negativeSide)
    {
        scale = Norm(scale);
        double tip = negativeSide ? seamCoordLogical + TipOverSeam : seamCoordLogical - TipOverSeam;
        return (tip - displayOriginAcrossLogical) / scale;
    }

    /// <summary>线端距接缝的留白（逻辑像素）—— 自检据此断言，改常量不会漏改断言。</summary>
    public static double SeamGapLogical => SeamGap;

    private static double Norm(double scale) => scale <= 0 ? 1 : scale;
}
