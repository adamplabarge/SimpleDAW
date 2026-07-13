using System.Windows.Input;

namespace SimpleDAW;

/// <summary>
/// A lightweight ICommand implementation for binding buttons to view-model methods.
///
/// <see cref="CanExecuteChanged"/> piggybacks on WPF's
/// <see cref="CommandManager.RequerySuggested"/>, so bound controls also
/// re-evaluate <see cref="CanExecute"/> automatically on common UI activity
/// (focus changes, mouse clicks, key presses) - not just when
/// <see cref="RaiseCanExecuteChanged"/> is explicitly called. That call is
/// still worth keeping at the point state actually changes (it gives instant
/// feedback rather than waiting for the next UI event), but this is a safety
/// net: if a future change adds a new state-affecting property and forgets to
/// call it, the command still catches up the next time the user interacts
/// with the UI, instead of staying stuck in a stale enabled/disabled state.
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public void RaiseCanExecuteChanged() =>
        CommandManager.InvalidateRequerySuggested();
}

/// <summary>
/// A lightweight ICommand implementation that accepts a typed command parameter.
/// See <see cref="RelayCommand"/> for why <see cref="CanExecuteChanged"/> is
/// routed through <see cref="CommandManager.RequerySuggested"/>.
/// </summary>
public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke((T?)parameter) ?? true;

    public void Execute(object? parameter) => _execute((T?)parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public void RaiseCanExecuteChanged() =>
        CommandManager.InvalidateRequerySuggested();
}
