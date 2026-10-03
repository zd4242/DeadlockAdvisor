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
    private readonly INotificationService _notifications;

    /// <param name="open">Opens a web page in the browser.</param>
    public AppUpdateViewModel(IAppUpdateService updates, ISettingsService settings, INotificationService notifications, ReactiveCommand<string, Unit> open)
    {
        _updates = updates;
        _settings = settings;
        _notifications = notifications;

        OpenCommand = ReactiveCommand.CreateFromObservable(() => open.Execute(Available!.Url));
        CheckNowCommand = ReactiveCommand.CreateFromTask(() => CheckAsync(manual: true));
        DismissCommand = ReactiveCommand.Create(() =>
        {
            _settings.Update(s => s.SkippedAppVersion = Available!.Version);
            Available = null;
        });
        settings.SettingsChanged
            .Select(s => s.CheckForAppUpdates)
            .DistinctUntilChanged()
            .Where(on => !on)
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

    /// <summary>Settings → Data's "Check now": says how it went, and shows a version that was dismissed.</summary>
    public ReactiveCommand<Unit, Unit> CheckNowCommand { get; }

    /// <summary>The version running: "0.2.0", or null for a build made outside the release workflow.</summary>
    public Version? Current => _updates.Current;

    /// <summary>
    /// One request to GitHub. At startup it says nothing unless a newer version is out: a failed check isn't
    /// worth interrupting anyone over.
    /// </summary>
    /// <param name="manual">Asked for: says how it went, and shows a version that was dismissed.</param>
    public async Task CheckAsync(bool manual = false)
    {
        if (_updates.Current is not { } current)
        {
            if (manual)
                _notifications.ShowInformation("This build wasn't made by the release workflow, so it has no version to compare with releases.");
            return;
        }
        if (!manual && !_settings.Current.CheckForAppUpdates)
            return;
        var latest = await _updates.LatestAsync();
        if (latest is null)
        {
            if (manual)
                _notifications.ShowError("Couldn't reach GitHub to check for a newer version. Try again later.", TimeSpan.FromSeconds(5));
            return;
        }
        _settings.Update(s => s.AppUpdateCheckedAt = DateTimeOffset.Now);
        var newer = Version.TryParse(latest.Version, out var version) && version > current;
        if (newer && (manual || latest.Version != _settings.Current.SkippedAppVersion && _settings.Current.CheckForAppUpdates))
            Available = latest;
        else if (manual)
            _notifications.ShowSuccess($"You have the newest version, {current}.", TimeSpan.FromSeconds(5));
    }
}
