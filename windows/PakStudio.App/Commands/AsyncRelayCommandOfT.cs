using System.Windows.Input;

namespace PakStudio.App.Commands;

public sealed class AsyncRelayCommand<T> : ICommand where T : class
{
    private readonly Func<T?, Task> _executeAsync;
    private readonly Func<T?, bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<T?, Task> executeAsync, Func<T?, bool>? canExecute = null)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter)
    {
        var value = parameter is T typed ? typed : default;
        return !_isExecuting && (_canExecute?.Invoke(value) ?? true);
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        var value = parameter is T typed ? typed : default;
        try
        {
            _isExecuting = true;
            CommandManager.InvalidateRequerySuggested();
            await _executeAsync(value).ConfigureAwait(true);
        }
        finally
        {
            _isExecuting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
