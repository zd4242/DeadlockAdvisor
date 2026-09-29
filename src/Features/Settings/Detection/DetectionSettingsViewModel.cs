using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Settings.Detection;

/// <summary>Where F9 works, how Detect from screen captures, and the hero strip it remembers per screen size.</summary>
public class DetectionSettingsViewModel : SettingsPageViewModel
{
    public const string AnywhereDescription =
        "Press F9 in the game to detect without switching to this window, which comes up once there's something to review. "
        + "While this is on, other apps don't get F9.";

    public DetectionSettingsViewModel(ISettingsService settings, IGlobalHotkeyService hotkey) : base(settings)
    {
        hotkey.Status
            .Subscribe(status => DetectFromAnywhereDescription = status switch
            {
                HotkeyStatus.Taken => AnywhereDescription + " Another app already has F9, so for now it only works while this window has focus.",
                HotkeyStatus.Unsupported => "Only available on Windows.",
                _ => AnywhereDescription,
            })
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

    [Reactive] public string DetectFromAnywhereDescription { get; private set; } = AnywhereDescription;

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

    [Reactive] public bool HasRememberedLayouts { get; private set; }
    [Reactive] public string RememberedLayouts { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> ForgetLayoutsCommand { get; }
}
