using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MonitorAlign.Converters;

/// <summary>
/// "#RRGGBB" → 画刷。
///
/// 同 <see cref="ThemeBrushConverter"/> 一个道理：ViewModel 不许碰 XAML，
/// 于是颜色在 VM 那边只是个字符串，到这里才变成画刷。
/// 解析失败给中性灰 —— 宁可显示一格灰，也别让绑定抛异常。
/// </summary>
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, string language)
    {
        Color c = Parse(value as string);
        return new SolidColorBrush(c);
    }

    public object ConvertBack(object value, System.Type targetType, object parameter, string language)
        => throw new System.NotSupportedException();

    public static Color Parse(string hex)
    {
        if (Services.EdgeGlowService.TryParseHex(hex, out Color c)) return c;
        return Color.FromArgb(255, 0x80, 0x80, 0x80);
    }
}
