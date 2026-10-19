namespace MonitorAlign.Core;

/// <summary>
/// 边缘泛光的全部可调参数（设置页 ↔ 存档 ↔ 服务共用一份）。
///
/// 从 <c>EdgeGlowService.cs</c> 里单独拆出来：服务那一半要碰窗口（WPF / WinUI 各自一份），
/// 而这一半是纯数据，两边项目用源码链接共用同一份定义，省得字段改漏一边。
/// </summary>
public sealed class EdgeGlowConfig
{
    /// <summary>接近灵敏度（倍数）：光标多近才开始起辉 = 120px ÷ 倍数。</summary>
    public double Sensitivity { get; set; } = 1.0;

    /// <summary>亮度 0..1。注意：亮度乘在**光强**上，不是乘在 alpha 上（见 GlowOverlay 头注释）。</summary>
    public double Brightness { get; set; } = 0.6;

    /// <summary>光晕单侧照射距离（逻辑像素）。这是"光能照到多远"，不限制上限（仅防异常值）。</summary>
    public double Distance { get; set; } = 96;

    /// <summary>
    /// 尾部白度（「加性强度」）0..1：0 = 尾部纯彩色（白底上会被压暗约 15%），
    /// 1 = 尾部全白（几乎不压暗，但颜色没了）。芯部恒为全白。
    /// </summary>
    public double TailWhiteness { get; set; } = 0.25;

    /// <summary>接缝自定义颜色：seamKey("#1->#2") → "#RRGGBB"。没有的用主题强调色。</summary>
    public System.Collections.Generic.Dictionary<string, string> Colors { get; set; } = new();
}
