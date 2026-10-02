using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow;

/// <summary>
/// The status bar's word that a newer version of the app is out: checked once a startup against GitHub's
/// newest release, unless Settings → Data turns it off. Dismissing it skips that version. A build made
/// outside the release workflow has no version to compare, so it never asks.
/// </summary>
public sealed class AppUpdateViewModel : ViewModelBase
{
    private readonly IAppUpdateService _updates;
    private readonly ISettingsService _settings;

    /// <param name="open">Opens a web page in the browser.</param>
    public AppUpdateViewModel(IAppUpdateService updates, ISettingsService settings, ReactiveCommand<string, Unit> open)
    {
        _updates = updates;
        _settings = settings;

        OpenCommand = ReactiveCommand.CreateFromObservable(() => open.Execute(Available!.Url));
        DismissCommand = ReactiveCommand.Create(() =>
        {
            _settings.Update(s => s.SkippedAppVersion = Available!.Version);
            Available = null;
        });
        settings.SettingsChanged
            .Where(s => !s.CheckForAppUpdates)
            .Subscribe(_ => Available = null)
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.Available)
            .Subscribe(available => Label = available is null ? "" : $"Version {available.Version} is out")
            .DisposeWith(Disposables);
    }

    /// <summary>The newer release, while it's to be shown.</summary>
    [Reactive] public AppRelease? Available { get; private set; }

    /// <summary>"Version 0.2.0 is out".</summary>
    [Reactive] public string Label { get; private set; } = "";

    /// <summary>The release's page, with what's new and the download.</summary>
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    public ReactiveCommand<Unit, Unit> DismissCommand { get; }

    /// <summary>One request to GitHub. Says nothing when it fails: it isn't worth interrupting anyone over.</summary>
    public async Task CheckAsync()
    {
        if (_updates.Current is not { } current || !_settings.Current.CheckForAppUpdates)
            return;
        var latest = await _updates.LatestAsync();
        if (latest is null || !Version.TryParse(latest.Version, out var version) || version <= current
            || latest.Version == _settings.Current.SkippedAppVersion || !_settings.Current.CheckForAppUpdates)
            return;
        Available = latest;
    }
}
