using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using MonitorAlign.ViewModels;

namespace MonitorAlign.Views;

/// <summary>
/// 扩展功能页：接缝标尺 → 目标分辨率。
///
/// 页面本身没有逻辑，全走 <see cref="SeamScaleViewModel"/>。
/// 唯一的生命周期职责：进入时刷新接缝清单、离开时把标尺窗口收掉 ——
/// 不收的话切走页面后屏幕上会留下四个幽灵覆盖层。
/// </summary>
public sealed partial class DevPage : Page
{
    public SeamScaleViewModel ViewModel { get; } =
        App.Services.GetRequiredService<SeamScaleViewModel>();

    public DevPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Activate();
        Unloaded += (_, _) => ViewModel.Deactivate();
    }
}
