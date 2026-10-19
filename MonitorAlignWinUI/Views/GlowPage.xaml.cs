using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MonitorAlign.ViewModels;

namespace MonitorAlign.Views;

/// <summary>
/// 边缘泛光设置页。
///
/// 参数/颜色全走 <see cref="GlowViewModel"/> 的双向绑定，这里只留两件纯视图的事：
///   ① 列表行的选中回调（ListView 的选中态是控件内部状态，绑定不到 VM 里）；
///   ② 取色对话框的开合（ContentDialog 是视图对象，只能在这里 ShowAsync）。
/// </summary>
public sealed partial class GlowPage : Page
{
    public GlowViewModel ViewModel { get; } =
        App.Services.GetRequiredService<GlowViewModel>();

    public GlowPage()
    {
        InitializeComponent();
        Diag.Log($"GlowPage 已构造：VM={(ViewModel == null ? "null" : "ok")} " +
                 $"内容={(Content as FrameworkElement)?.GetType().Name ?? "null"}");
        Loaded += (_, _) =>
        {
            Diag.Log($"GlowPage Loaded：实际 {ActualWidth:0.#}x{ActualHeight:0.#}");
            ViewModel.Activate();
        };
    }

    private void SeamList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is GlowSeamRow row)
            ViewModel.Select(row.Seam);
    }

    // ------------------------------------------------------------ 取色弹窗

    private bool _pickerOpen;

    /// <summary>对话框的展示与重入保护只属于视图层。</summary>
    private async void OpenColorPicker_Click(object sender, RoutedEventArgs e)
    {
        if (_pickerOpen || !ViewModel.HasSelection || XamlRoot == null) return;
        _pickerOpen = true;
        bool committed = false;
        ViewModel.BeginColorEdit();
        try
        {
            PickDialog.XamlRoot = XamlRoot;
            var result = await PickDialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                ViewModel.CommitColorEdit();
                committed = true;
            }
        }
        catch (System.Exception ex)
        {
            Diag.Log("取色对话框失败：" + ex);
        }
        finally
        {
            try { if (!committed) ViewModel.CancelColorEdit(); }
            finally { _pickerOpen = false; }
        }
    }
}
