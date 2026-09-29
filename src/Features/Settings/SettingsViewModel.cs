using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using Avalonia.Media;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Settings.Data;
using DeadlockAdvisor.Features.Settings.Detection;
using DeadlockAdvisor.Features.Settings.General;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Settings;

/// <summary>A page of the Settings page, as its sidebar lists it. <see cref="Icon"/> is a stroked outline in a 16×16 box.</summary>
public sealed record SettingsCategory(string Name, string Summary, Geometry Icon, SettingsPageViewModel Page);

/// <summary>The Settings page: a sidebar of categories beside the chosen one's options, all saved as they change.</summary>
public class SettingsViewModel : ViewModelBase
{
    public SettingsViewModel(GeneralSettingsViewModel general, DetectionSettingsViewModel detection, DataSettingsViewModel data)
    {
        General = general.DisposeWith(Disposables);
        Detection = detection.DisposeWith(Disposables);
        Data = data.DisposeWith(Disposables);
        Categories =
        [
            new("General", "Zoom, what the app reopens on, and the model editors.",
                Geometry.Parse("M2,4 H4.7 M8.3,4 H14 M2,8 H9.2 M12.8,8 H14 M2,12 H3.2 M6.8,12 H14 M6.5,4 m-1.8,0 a1.8,1.8 0 1,0 3.6,0 a1.8,1.8 0 1,0 -3.6,0 "
                + "M11,8 m-1.8,0 a1.8,1.8 0 1,0 3.6,0 a1.8,1.8 0 1,0 -3.6,0 M5,12 m-1.8,0 a1.8,1.8 0 1,0 3.6,0 a1.8,1.8 0 1,0 -3.6,0"),
                General),
            new("Detection", "Reading the match off the game's top bar (F9).",
                Geometry.Parse("M1.5,2.5 H14.5 V11.5 H1.5 Z M5,14.5 H11 M8,11.5 V14.5 "
                               + "M4,6.5 V4.5 H6 M10,4.5 H12 V6.5 M4,7.5 V9.5 H6 M10,9.5 H12 V7.5"),
                Detection),
            new("Data", "Where the data and art live, and checking for patches.",
                Geometry.Parse("M2.5,4 A5.5,2 0 1,0 13.5,4 A5.5,2 0 1,0 2.5,4 M2.5,4 V12 A5.5,2 0 0,0 13.5,12 V4 M2.5,8 A5.5,2 0 0,0 13.5,8"),
                Data),
        ];
        SelectedCategory = Categories[0];

        CloseCommand = ReactiveCommand.Create(() => _closeRequested.OnNext(Unit.Default));
        _closeRequested.DisposeWith(Disposables);
    }

    public GeneralSettingsViewModel General { get; }
    public DetectionSettingsViewModel Detection { get; }
    public DataSettingsViewModel Data { get; }

    public IReadOnlyList<SettingsCategory> Categories { get; }

    [Reactive] public SettingsCategory SelectedCategory { get; set; }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    /// <summary>Back, Escape or the mouse's back button: return to the page it was opened from.</summary>
    public IObservable<Unit> CloseRequested => _closeRequested;
    private readonly Subject<Unit> _closeRequested = new();

    /// <summary>Re-read what the rest of the app may have changed since the page was last shown.</summary>
    public void Refresh()
    {
        foreach (var category in Categories)
            category.Page.Refresh();
    }
}
