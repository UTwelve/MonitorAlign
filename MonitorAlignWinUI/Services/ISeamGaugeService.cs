using System;
using MonitorAlign.Core;

namespace MonitorAlign.Services;

/// <summary>
/// 接缝标尺（扩展功能页用的那套覆盖层）。
///
/// 抽接口的理由同 <see cref="IAlignSession"/> / <see cref="IEdgeGlowService"/>：
/// ViewModel 不许引用 <c>Microsoft.UI.Xaml.*</c>，而标尺就是四个覆盖层窗口。
/// VM 只通过它"摆标尺 / 收标尺 / 归零"，位移从 <see cref="OffsetsChanged"/> 回来。
/// </summary>
public interface ISeamGaugeService
{
    /// <summary>标尺是否已经摆在屏幕上。</summary>
    bool Active { get; }

    /// <summary>当前摆的是哪条接缝。</summary>
    Seam Current { get; }

    /// <summary>生成两对标尺（先收掉旧的）。</summary>
    void Place(Seam seam);

    /// <summary>收掉所有标尺。</summary>
    void Clear();

    /// <summary>两枚可拖标尺都归零。</summary>
    void ResetOffsets();

    /// <summary>
    /// 程序化把第 <paramref name="pair"/> 对（0 = 上/左，1 = 下/右）的可拖标尺推到指定位移。
    ///
    /// 「标尺归零」已经是同一类的程序化写入，这里只是把它推广到任意值：
    /// 给 <c>--gauge</c> 这类无鼠标的自动验收留一条通路
    /// （锁屏 / 无人值守时鼠标事件根本不会送达窗口）。
    /// </summary>
    void SetPairOffset(int pair, double physical);

    /// <summary>
    /// 位移变化：参数是 (第一枚位移, 第二枚位移)，单位 = 副屏物理像素。
    /// </summary>
    event Action<double, double> OffsetsChanged;
}
