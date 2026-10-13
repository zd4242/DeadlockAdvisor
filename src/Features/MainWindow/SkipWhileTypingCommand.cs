using System.Windows.Input;

namespace DeadlockAdvisor.Features.MainWindow;

/// <summary>
/// A shortcut's command that stands aside while a text box has focus, so a key that edits text, like Delete,
/// still does in one: a key binding that can't run leaves the key to the box.
/// </summary>
public sealed class SkipWhileTypingCommand(ICommand inner, Func<bool> isTyping) : ICommand
{
    public ICommand Inner => inner;

    public event EventHandler? CanExecuteChanged
    {
        add => inner.CanExecuteChanged += value;
        remove => inner.CanExecuteChanged -= value;
    }

    public bool CanExecute(object? parameter) => !isTyping() && inner.CanExecute(parameter);

    public void Execute(object? parameter) => inner.Execute(parameter);
}
