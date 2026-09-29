using System.Reactive;
using Avalonia.Input;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Settings.Shortcuts;

/// <summary>One rebindable key: what it does, the key it's on, and why the last key pressed for it wasn't taken.</summary>
public class ShortcutRowViewModel : ReactiveObject
{
    private readonly string _description;

    public ShortcutRowViewModel(ShortcutAction action, string title, string description, Func<ShortcutRowViewModel, KeyGesture?, string?> assign)
    {
        Action = action;
        Title = title;
        _description = description;
        Description = description;
        RecordCommand = ReactiveCommand.Create<KeyGesture?>(gesture => Note(assign(this, gesture)));
        ResetCommand = ReactiveCommand.Create(() => Note(assign(this, ShortcutKeys.Defaults[action])), this.WhenAnyValue(row => row.IsDefault, isDefault => !isDefault));
        this.WhenAnyValue(row => row.IsRecording)
            .Subscribe(recording =>
            {
                if (recording)
                    Note(null);
            });
    }

    public ShortcutAction Action { get; }
    public string Title { get; }

    /// <summary>What the key does, or while there's something to say about the last key pressed for it, that.</summary>
    [Reactive] public string Description { get; private set; }

    [Reactive] public KeyGesture? Gesture { get; private set; }
    [Reactive] public bool IsDefault { get; private set; }

    /// <summary>Set by the view while it waits for a key.</summary>
    [Reactive] public bool IsRecording { get; set; }

    /// <summary>A key pressed for this, or null to take its key away.</summary>
    public ReactiveCommand<KeyGesture?, Unit> RecordCommand { get; }

    public ReactiveCommand<Unit, Unit> ResetCommand { get; }

    public void Show(KeyGesture? gesture)
    {
        Gesture = gesture;
        IsDefault = Equals(gesture, ShortcutKeys.Defaults[Action]);
    }

    public void Note(string? note) => Description = note ?? _description;
}
