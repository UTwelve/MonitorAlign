namespace MonitorAlign.Core;

/// <summary>
/// 换算核心。沿接缝法线方向（垂直接缝取 Y、水平接缝取 X）工作。
///
/// 推导（见需求文档 2.2）：
///   定义 k = 物理上边缘 − 逻辑上边缘 × 缩放，跨屏连续 ⟺ k_主 == k_副
///   用户把副屏参考物从基准高度 p 拖到 s 时才"看起来对齐"，则当前误差 Δ = s − p
///   消除误差需令 k_副 变成 k_主，解得：  新逻辑位置 = 旧逻辑位置 − Δ
/// 即：参考物往哪拖，系统偏移就往反方向补。
/// </summary>
public static class AlignmentSolver
{
    /// <summary>当前跨屏跳动量（逻辑像素）。正值表示副屏内容看起来偏下/偏右。</summary>
    public static int CurrentError(int baseAnchor, int markerPos) => markerPos - baseAnchor;

    /// <summary>由拖动结果求新的逻辑位置。</summary>
    public static int SolveNewPosition(int baseAnchor, int markerPos, int currentLogicalPos)
        => currentLogicalPos - (markerPos - baseAnchor);

    /// <summary>
    /// 由标尺拖动量求目标屏的新逻辑位置，四个方位通用。
    ///
    /// 参数 <paramref name="seamVertical"/> 决定补偿落在哪根轴上：
    ///   左右并排（竖直缝）→ 改 Y；上下叠放（水平缝）→ 改 X。
    /// 两个方向都是"标尺往正方向拖，逻辑位置就往反方向补"，
    /// 所以这里只有一处符号，不存在"副屏在左对、在右错"的空间。
    /// </summary>
    public static (int X, int Y) SolvePosition(bool seamVertical, int currentX, int currentY, int delta)
        => seamVertical ? (currentX, currentY - delta)
                        : (currentX - delta, currentY);

    /// <summary>逻辑像素 → 毫米（用于物理校准读数）。</summary>
    public static double ToMm(int px, double mmPerPixel) => px * mmPerPixel;

    /// <summary>
    /// 两屏缩放不一致时，跨屏落差随高度线性变化，无法全高度同时对齐。
    /// 返回在给定高度处的落差（逻辑像素）。
    /// </summary>
    public static double ErrorAtHeight(double baseScale, double targetScale, double kBase, double kTarget, double height)
        => (kBase + height * baseScale) - (kTarget + height * targetScale);
}

/*
 * 「保存配置 / 恢复配置」已移除 —— 不需要自己的 json 文件，原因：
 *
 *   1) 系统本身就是持久层。补偿是通过 ChangeDisplaySettingsEx(CDS_UPDATEREGISTRY) 写进
 *      显示配置的，重启后依然生效，不需要我们再存一份。
 *   2) 存一份反而有害。偏移是相对"当时"排列的增量，一旦用户在显示设置里改过排列，
 *      这份增量就是错的，套回去会把排列搞乱。
 *   3) 「撤销」只需要内存里的启动基线（MainWindow._baseline），进程内就够。
 */
