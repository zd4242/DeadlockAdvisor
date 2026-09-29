using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Input;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;

namespace DeadlockAdvisor.Features.Settings.Shortcuts;

/// <summary>The Match page's keys for Detect and Random, each moved by clicking it and pressing the new one.</summary>
public class ShortcutsSettingsViewModel : SettingsPageViewModel
{
    public ShortcutsSettingsViewModel(ISettingsService settings, IGlobalHotkeyService hotkey) : base(settings)
    {
        Detect = new(ShortcutAction.Detect, "Detect from screen",
            "Read the match off the game's top bar. While Detection → Detect from any app is on, this works from inside the game too.", Assign);
        Randomize = new(ShortcutAction.Randomize, "Random",
            "Fill the match with random heroes, for trying things out. The Random keys only work while General → Show the Random buttons is on.", Assign);
        RandomizeKeepSelf = new(ShortcutAction.RandomizeKeepSelf, "Random, keeping your hero", "Redraw everyone but you.", Assign);
        RandomizeKeepTeam = new(ShortcutAction.RandomizeKeepTeam, "Random enemies, keeping your team", "Redraw only the enemy team.", Assign);
        Rows = [Detect, Randomize, RandomizeKeepSelf, RandomizeKeepTeam];

        settings.SettingsChanged
            .Subscribe(s =>
            {
                foreach (var row in Rows)
                    row.Show(s.Gesture(row.Action));
            })
            .DisposeWith(Disposables);

        // Detect's key is held system-wide, so pressing it to record would detect instead of reaching the page.
        var suspension = new SerialDisposable().DisposeWith(Disposables);
        Rows.Select(row => row.WhenAnyValue(r => r.IsRecording))
            .CombineLatest(recording => recording.Any(r => r))
            .DistinctUntilChanged()
            .Subscribe(recording => suspension.Disposable = recording ? hotkey.Suspend() : null)
            .DisposeWith(Disposables);

        ResetAllCommand = ReactiveCommand.Create(ResetAll, settings.SettingsChanged.Select(s => s.Shortcuts.Count > 0));
    }

    public ShortcutRowViewModel Detect { get; }
    public ShortcutRowViewModel Randomize { get; }
    public ShortcutRowViewModel RandomizeKeepSelf { get; }
    public ShortcutRowViewModel RandomizeKeepTeam { get; }
    public IReadOnlyList<ShortcutRowViewModel> Rows { get; }

    public ReactiveCommand<Unit, Unit> ResetAllCommand { get; }

    public override void Refresh()
    {
        foreach (var row in Rows)
            row.Note(null);
    }

    /// <summary>Put <paramref name="row"/> on <paramref name="gesture"/>, or say why not.</summary>
    private string? Assign(ShortcutRowViewModel row, KeyGesture? gesture)
    {
        if (gesture is not null && ShortcutKeys.Problem(gesture) is { } problem)
            return problem;
        var previous = Current.Gesture(row.Action);
        if (Equals(gesture, previous))
            return null;

        // Taking another shortcut's key swaps the two, so neither is lost.
        var displaced = gesture is null ? null : Rows.FirstOrDefault(other => other != row && Equals(Current.Gesture(other.Action), gesture));
        Settings.Update(s =>
        {
            if (displaced is not null)
                s.SetGesture(displaced.Action, previous);
            s.SetGesture(row.Action, gesture);
        });
        if (displaced is null)
            return null;
        return previous is null
            ? $"Taken from {displaced.Title}, which now has no key."
            : $"Taken from {displaced.Title}, which moved to {ShortcutKeys.Label(previous)}.";
    }

    private void ResetAll()
    {
        Settings.Update(s => s.Shortcuts.Clear());
        Refresh();
    }
}
