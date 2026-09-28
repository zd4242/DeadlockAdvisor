using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Settings.Detection;

/// <summary>How Detect from screen captures, and the hero strip it remembers per screen size.</summary>
public class DetectionSettingsViewModel : SettingsPageViewModel
{
    public DetectionSettingsViewModel(ISettingsService settings) : base(settings)
    {
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
