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
    public DetectionSettingsViewModel(ISettingsService settings, IGlobalHotkeyService hotkey) : base(settings)
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

    [Reactive] public bool HasRememberedLayouts { get; private set; }
    [Reactive] public string RememberedLayouts { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> ForgetLayoutsCommand { get; }

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
