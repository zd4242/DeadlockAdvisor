using System.Windows.Input;
using Avalonia.Input;

namespace DeadlockAdvisor.Features.MainWindow;

/// <summary>A rebindable key and the command it runs, which the window turns into a <see cref="KeyBinding"/>.</summary>
public sealed record ShortcutBinding(KeyGesture Gesture, ICommand Command, object Parameter);
