using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MonitorAlign.Core;

namespace MonitorAlign.ViewModels;

/// <summary>
/// 设置页：主题三态循环 / 开机自启 / 关于。
///
/// 主题在这里只是个 0/1/2 的下标 —— <c>ElementTheme</c> 是 XAML 类型，VM 不能碰，
/// 于是 VM 存下标、视图层（SettingsPage）把它翻译成 ElementTheme 再套到窗口根元素上。
/// 与 WPF 版一样**不持久化**主题：每次启动都回到"跟随系统"。
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    public const int ThemeAuto = 0;
    public const int ThemeLight = 1;
    public const int ThemeDark = 2;

    private int _theme = ThemeAuto;
    private bool _autoStart;

    public event PropertyChangedEventHandler PropertyChanged;

    public SettingsViewModel()
    {
        CycleThemeCommand = new RelayCommand(CycleTheme);
    }

    public int ThemeIndex
    {
        get => _theme;
        set
        {
            if (value < ThemeAuto || value > ThemeDark || _theme == value) return;
            _theme = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThemeName));
            OnPropertyChanged(nameof(ThemeTooltip));
        }
    }

    public string ThemeName => _theme switch
    {
        ThemeLight => Loc.ThemeLight,
        ThemeDark => Loc.ThemeDark,
        _ => Loc.ThemeAuto,
    };

    public string ThemeTooltip => Loc.Fmt(Loc.TooltipTheme, ThemeName);

    public ICommand CycleThemeCommand { get; }

    private void CycleTheme() => ThemeIndex = (ThemeIndex + 1) % 3;

    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (_autoStart == value) return;
            _autoStart = value;
            Services.AutoStart.Set(value);
            OnPropertyChanged();
        }
    }

    /// <summary>进页面时刷新一次（注册表可能被别处改过）。</summary>
    public void Refresh()
    {
        _autoStart = Services.AutoStart.Get();
        OnPropertyChanged(nameof(AutoStart));
    }

    public Uri AuthorUri => new(Loc.AuthorUrl);

    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
