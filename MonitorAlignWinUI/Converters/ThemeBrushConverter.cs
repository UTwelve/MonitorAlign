using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace MonitorAlign.Converters;

/// <summary>
/// 令牌名 → 画刷。
///
/// 为什么需要它：ViewModel 不许引用 <c>Microsoft.UI.Xaml.*</c>（winui3-skills 硬规则），
/// 于是它没法自己造 <c>Brush</c>；但它知道"这块该用哪个令牌"。
/// 于是 VM 只给**令牌名**（字符串），由视图层（x:Bind 走本转换器、
/// code-behind 走 <see cref="From"/>）去资源字典里取。
/// 明暗切换也因此自动跟随 —— 令牌走的是 ThemeResource 体系。
/// </summary>
public sealed class ThemeBrushConverter : IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, string language)
        => From(value as string);

    public object ConvertBack(object value, System.Type targetType, object parameter, string language)
        => throw new System.NotSupportedException();

    /// <summary>按令牌名取画刷；找不到返回 null（调用方按透明处理）。</summary>
    public static Brush From(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (Application.Current?.Resources is not ResourceDictionary root) return null;
        string theme = App.MainWindow?.Content is FrameworkElement element
            ? element.ActualTheme.ToString()
            : Application.Current.RequestedTheme.ToString();
        return Lookup(root, key, theme);
    }

    /// <summary>
    /// 递归查找：优先实际主题和合并字典，最后回落到普通资源，避免命中应用默认主题的缓存值。
    ///
    /// 为什么要递归：令牌定义在 <c>Themes/MaTokens.xaml</c> 里，它是 App 的**合并字典**，
    /// 而且键只存在于它自己的 ThemeDictionaries 中 —— 直接查 Application.Resources
    /// 只在少数情况下命中，漏了就会画出一片透明（表现为"缩略图空了"）。
    /// </summary>
    private static Brush Lookup(ResourceDictionary dict, string key, string theme)
    {
        if (dict.ThemeDictionaries.TryGetValue(theme, out object td) && td is ResourceDictionary themeDict
            && themeDict.TryGetValue(key, out object v2) && v2 is Brush b2)
            return b2;

        foreach (var merged in dict.MergedDictionaries)
        {
            var found = Lookup(merged, key, theme);
            if (found != null) return found;
        }

        return dict.TryGetValue(key, out object v) && v is Brush b ? b : null;
    }
}
