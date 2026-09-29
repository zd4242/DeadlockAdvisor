using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Input;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Settings.General;

/// <summary>Zoom, and what the app reopens on.</summary>
public class GeneralSettingsViewModel : SettingsPageViewModel
{
    public GeneralSettingsViewModel(ISettingsService settings, ICommand zoomIn, ICommand zoomOut, ICommand resetZoom) : base(settings)
    {
        ZoomInCommand = zoomIn;
        ZoomOutCommand = zoomOut;
        ResetZoomCommand = resetZoom;

        settings.SettingsChanged
            .Select(s => ZoomLevels.Clamp(s.ZoomIndex))
            .DistinctUntilChanged()
            .Subscribe(index => ZoomText = $"{Math.Round(ZoomLevels.Steps[index] * 100):0}%")
            .DisposeWith(Disposables);
    }

    [Reactive] public string ZoomText { get; private set; } = "";

    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ResetZoomCommand { get; }

    public bool ReopenLastPage
    {
        get => Current.ReopenLastPage;
        set => Change(s => s.ReopenLastPage = value);
    }

    public bool ReopenLastMatch
    {
        get => Current.ReopenLastMatch;
        set => Change(s => s.ReopenLastMatch = value);
    }

    public bool ShowRandomButtons
    {
        get => Current.ShowRandomButtons;
        set => Change(s => s.ShowRandomButtons = value);
    }
}
