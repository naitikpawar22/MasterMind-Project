using System.Windows.Input;

namespace GeminiDesktop.Helpers;

/// <summary>
/// Synchronous ICommand implementation with optional CanExecute predicate.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _execute();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Generic synchronous ICommand implementation.
/// </summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Predicate<T?>? _canExecute;

    public RelayCommand(Action<T?> execute, Predicate<T?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (parameter is null && typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null)
        {
            return _canExecute?.Invoke(default) ?? true;
        }

        return parameter is T typed
            ? _canExecute?.Invoke(typed) ?? true
            : _canExecute?.Invoke(default) ?? true;
    }

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        var value = parameter is T typed ? typed : default;
        _execute(value);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Asynchronous ICommand implementation with re-entrancy protection.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _executeAsync;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onException;
    private bool _isExecuting;

    public AsyncRelayCommand(
        Func<Task> executeAsync,
        Func<bool>? canExecute = null,
        Action<Exception>? onException = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
        _onException = onException;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _executeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _onException?.Invoke(ex);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
