using System.Collections.Generic;
using MonitorAlign.Core;

namespace MonitorAlign.Services;

/// <summary>
/// 边缘泛光服务（界面无关的那一半）。
///
/// 抽接口的原因同 <see cref="IAlignSession"/>：ViewModel 不许引用
/// <c>Microsoft.UI.Xaml.*</c>，而泛光偏偏就是一叠 WinUI 窗口。
/// VM 只通过它读写参数、拿接缝清单、下发颜色（颜色一律用 "#RRGGBB" 字符串，
/// 不碰 Color 结构）。
/// </summary>
public interface IEdgeGlowService
{
    /// <summary>可调参数（设置页直接改，改完调 <see cref="ApplyConfig"/>）。</summary>
    EdgeGlowConfig Config { get; }

    bool Enabled { get; }

    /// <summary>接缝清单变了（排列变化 / 首次进入）就重建覆盖层。</summary>
    void UpdateSeams(IReadOnlyList<Seam> seams);

    /// <summary>参数变化：灵敏度/亮度立即生效；照射距离需要重建窗口。</summary>
    void ApplyConfig(bool layoutChanged);

    /// <summary>颜色表改了：重建覆盖层取新色。</summary>
    void RefreshColors();

    void Start(IReadOnlyList<Seam> seams);
    void Stop();

    /// <summary>某接缝是否有自定义色。</summary>
    bool HasCustomColor(Seam s);

    /// <summary>
    /// 这条接缝**实际会发出什么颜色**（自定义色优先，否则主题强调色）。
    /// 取色器要用它做初值 —— 否则用户点开一条走主题色的接缝，取色器却停在别的颜色上。
    /// </summary>
    Windows.UI.Color ColorOf(Seam s);

    /// <summary>接缝的稳定键（"#1->#2"），存档与自定义色表都用它。</summary>
    string KeyOf(Seam s);
}
