using MonitorAlign.Core;

namespace MonitorAlign.Services;

/// <summary>
/// 接缝的说明文案（工具提示 / 列表行共用）。
///
/// 与 WPF 版 <c>MainWindow.SeamText</c> 同义，抽出来是因为对齐页与泛光页都要显示它。
/// </summary>
public static class SeamText
{
    public static string Describe(Seam s) =>
        s.Unreachable
            ? Loc.Fmt(Loc.ComboUnreachable, s.Target.Index)
            : Loc.Fmt(Loc.ComboItem, s.Label, s.DirectionText, s.Depth);
}
