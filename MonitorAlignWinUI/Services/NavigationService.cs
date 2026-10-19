using Microsoft.UI.Xaml.Controls;

namespace MonitorAlign.Services;

/// <summary>类型安全导航服务（winui3-skills 模式）。ShellPage 持唯一 Frame。</summary>
public interface INavigationService
{
    void NavigateTo<TPage>() where TPage : Page;
}

/// <summary>
/// 导航实现：直接转发给 Shell 的 Frame。WinUI 3 的 Frame 页面缓存默认不启用——
/// 对齐页有会话状态（覆盖层/标尺），必须 <c>NavigationCacheMode.Required</c>（WPF 版同一教训）。
/// </summary>
public sealed class NavigationService : INavigationService
{
    public void NavigateTo<TPage>() where TPage : Page
    {
        if (App.MainWindow?.Content is Views.ShellPage shell)
            shell.Frame.Navigate(typeof(TPage));
    }
}
