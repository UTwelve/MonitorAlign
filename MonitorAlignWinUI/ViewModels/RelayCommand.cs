using System;
using System.Windows.Input;

namespace MonitorAlign.ViewModels;

/// <summary>
/// 极简命令。ViewModel 层用 <c>System.Windows.Input.ICommand</c>（不是 XAML 命名空间），
/// 所以不违反"ViewModel 不得引用 Microsoft.UI.Xaml.*"这条规则。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

    public RelayCommand(Action execute, Func<bool> canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler CanExecuteChanged;

    public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object parameter) => _execute();

    /// <summary>外部条件变了（比如 HasChanges 翻转）时通知按钮刷新可用态。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
