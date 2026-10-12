using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Input;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Settings.Detection;

/// <summary>Where Detect's key works, how Detect from screen captures, and the hero strip it remembers per screen size.</summary>
public class DetectionSettingsViewModel : SettingsPageViewModel
{
    public DetectionSettingsViewModel(ISettingsService settings, IGlobalHotkeyService hotkey, IScoreboardKeyService scoreboardKey) : base(settings)
    {
        settings.SettingsChanged
            .Select(s => s.Gesture(ShortcutAction.Detect))
            .DistinctUntilChanged()
            .CombineLatest(hotkey.Status, AnywhereDescription)
            .Subscribe(description => DetectFromAnywhereDescription = description)
            .DisposeWith(Disposables);

        settings.SettingsChanged
            .Select(s => string.Join(", ", s.VisionGeometry.Keys.Order(StringComparer.Ordinal).Select(key => key.Replace("x", " × "))))
            .DistinctUntilChanged()
            .Subscribe(screens =>
            {
                HasRememberedLayouts = screens.Length > 0;
                RememberedLayouts = "Detect remembers where the hero strip sits on each screen size, so it can skip the search next time. "
                                    + (HasRememberedLayouts ? $"Remembered for {screens}." : "None remembered yet.");
            })
            .DisposeWith(Disposables);

        settings.SettingsChanged
            .Select(s => s.Get())
            .DistinctUntilChanged()
            .CombineLatest(scoreboardKey.Status, DescribeScoreboard)
            .Subscribe(description => ScoreboardDescription = description)
            .DisposeWith(Disposables);
        settings.SettingsChanged
            .Select(s => s.Get())
            .DistinctUntilChanged()
            .Subscribe(key => ScoreboardKey = new KeyGesture(key))
            .DisposeWith(Disposables);
        RecordScoreboardKeyCommand = ReactiveCommand.Create<KeyGesture?>(RecordScoreboardKey);
        ResetScoreboardKeyCommand = ReactiveCommand.Create(() => RecordScoreboardKey(new KeyGesture(ScoreboardKeys.Default)),
            settings.SettingsChanged.Select(s => s.Get() != ScoreboardKeys.Default));

        ForgetLayoutsCommand = ReactiveCommand.Create(() => Settings.Update(s => s.VisionGeometry.Clear()),
            this.WhenAnyValue(vm => vm.HasRememberedLayouts));
    }

    public bool DetectFromAnywhere
    {
        get => Current.DetectFromAnywhere;
        set => Change(s => s.DetectFromAnywhere = value);
    }

    [Reactive] public string DetectFromAnywhereDescription { get; private set; } = "";

    public bool ComeUpForReview
    {
        get => Current.ComeUpForReview;
        set => Change(s => s.ComeUpForReview = value);
    }

    public bool SoundOnDetect
    {
        get => Current.SoundOnDetect;
        set => Change(s => s.SoundOnDetect = value);
    }

    public bool MinimizeToDetect
    {
        get => Current.MinimizeToDetect;
        set => Change(s => s.MinimizeToDetect = value);
    }

    public bool KeepUnreadCaptures
    {
        get => Current.KeepUnreadCaptures;
        set => Change(s => s.KeepUnreadCaptures = value);
    }

    public bool KeepDetectionCaptures
    {
        get => Current.KeepDetectionCaptures;
        set => Change(s => s.KeepDetectionCaptures = value);
    }

    public bool AutoApplyDetect
    {
        get => Current.AutoApplyDetect;
        set => Change(s => s.AutoApplyDetect = value);
    }

    public bool RememberCorrections
    {
        get => Current.RememberCorrections;
        set => Change(s => s.RememberCorrections = value);
    }

    public bool ReadBuildsOnTab
    {
        get => Current.ReadBuildsOnTab;
        set => Change(s => s.ReadBuildsOnTab = value);
    }

    private const string _scoreboardKeyHelp = "The key the game's scoreboard is on. Click, then press it; Delete goes back to Tab.";

    /// <summary>What reading the scoreboard does.</summary>
    [Reactive] public string ScoreboardDescription { get; private set; } = "";

    /// <summary>What the scoreboard key is, or while there's something to say about the last key pressed for it, that.</summary>
    [Reactive] public string ScoreboardKeyNote { get; private set; } = _scoreboardKeyHelp;

    [Reactive] public KeyGesture? ScoreboardKey { get; private set; }

    /// <summary>Set by the view while it waits for a key.</summary>
    [Reactive] public bool IsRecordingScoreboardKey { get; set; }

    /// <summary>A key pressed for the scoreboard, or null (Delete) to go back to Tab.</summary>
    public ReactiveCommand<KeyGesture?, Unit> RecordScoreboardKeyCommand { get; }

    public ReactiveCommand<Unit, Unit> ResetScoreboardKeyCommand { get; }

    public bool KeepScoreboardCaptures
    {
        get => Current.KeepScoreboardCaptures;
        set => Change(s => s.KeepScoreboardCaptures = value);
    }

    [Reactive] public bool HasRememberedLayouts { get; private set; }
    [Reactive] public string RememberedLayouts { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> ForgetLayoutsCommand { get; }

    private void RecordScoreboardKey(KeyGesture? gesture)
    {
        gesture ??= new KeyGesture(ScoreboardKeys.Default);
        if (ScoreboardKeys.Problem(gesture) is { } problem)
        {
            ScoreboardKeyNote = problem;
            return;
        }
        ScoreboardKeyNote = _scoreboardKeyHelp;
        Change(s => s.ScoreboardKey = gesture.Key.ToString(), nameof(ScoreboardKey));
    }

    public static string DescribeScoreboard(Key key, ScoreboardKeyStatus status)
    {
        if (status == ScoreboardKeyStatus.Unsupported)
            return "Only available on Windows.";
        var label = ScoreboardKeys.Label(key);
        return $"While the game is in front and you hold {label} (its scoreboard key: move it here if you moved it in the game), quietly read the board into the match you've applied: "
               + "each hero's net worth now. Nothing is shown, played or focused, and a board that isn't your match's is ignored. "
               + "Press Detect first so there is a match. The game has to be borderless or windowed, as for Detect.";
    }

    public static string AnywhereDescription(KeyGesture? gesture, HotkeyStatus status)
    {
        if (status == HotkeyStatus.Unsupported)
            return "Only available on Windows.";
        if (gesture is null)
            return "Detect from screen has no key. Give it one under Shortcuts.";

        var key = ShortcutKeys.Label(gesture);
        var description = $"Press {key} in the game to detect without switching to this window, which comes up once there's something to review. "
                          + $"While this is on, other apps don't get {key}.";
        return status == HotkeyStatus.Taken
            ? description + $" Another app already has {key}, so for now it only works while this window has focus."
            : description;
    }
}
