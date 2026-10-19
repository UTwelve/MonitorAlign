using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MonitorAlign.ViewModels;

namespace MonitorAlign.Views;

/// <summary>
/// 设置页。唯一"视图专属"的活儿是把主题下标翻译成 <c>ElementTheme</c> ——
/// 这个类型是 XAML 的，ViewModel 不许碰，于是翻译放在这一层。
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; } =
        App.Services.GetRequiredService<SettingsViewModel>();

    public SettingsPage()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.Refresh();
        ApplyTheme(ViewModel.ThemeIndex);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelChanged;
    }

    private void OnViewModelChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(SettingsViewModel.ThemeIndex))
            ApplyTheme(ViewModel.ThemeIndex);
    }

    private static void ApplyTheme(int index)
    {
        if (App.MainWindow?.Content is not FrameworkElement root) return;

        root.RequestedTheme = index switch
        {
            SettingsViewModel.ThemeLight => ElementTheme.Light,
            SettingsViewModel.ThemeDark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        Diag.Log($"主题切换为 {root.RequestedTheme}");
    }
}
