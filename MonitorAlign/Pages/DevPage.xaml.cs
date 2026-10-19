using System.Windows;

namespace MonitorAlign.Pages;

/// <summary>「开发中」占位页。图标跟随当前选中的导航项。</summary>
public partial class DevPage : System.Windows.Controls.Page
{
    public DevPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (Application.Current.MainWindow is not MainWindow mw) return;

            // 窗口是 SizeToContent：本页高度对齐到对齐页，切页时窗口不抖
            Root.MinHeight = mw.PageAlignContentHeight;

            if (mw.Nav.SelectedItem is Wpf.Ui.Controls.NavigationViewItem item &&
                item.Icon is Wpf.Ui.Controls.SymbolIcon si)
                DevIcon.Symbol = si.Symbol;
        };
    }
}
