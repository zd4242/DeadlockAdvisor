using System.IO;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow;

public enum AppUpdateState
{
    /// <summary>Nothing to show: up to date, not checked, or dismissed.</summary>
    None,

    /// <summary>A newer version is out.</summary>
    Available,

    /// <summary>Downloading it, to install in place of this one.</summary>
    Downloading,

    /// <summary>Installed: the next start runs it.</summary>
    Ready,
}

/// <summary>
/// The status bar's word that a newer version of the app is out: checked once a startup against GitHub's
/// newest release, unless Settings → Data turns it off. On Windows, Update downloads it in the background
/// and installs it in place of this exe, so the next start runs it, and Restart now starts it straight
/// away. Elsewhere, or where the exe's folder can't be written to, it opens the release page instead.
/// Dismissing it skips that version. A build made outside the release workflow has no version to compare,
/// so it never asks.
/// </summary>
public sealed class AppUpdateViewModel : ViewModelBase
{
    private static readonly TimeSpan _toastTime = TimeSpan.FromSeconds(5);

    private readonly IAppUpdateService _updates;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly ReactiveCommand<string, Unit> _open;
    private readonly Subject<Unit> _restartRequested = new();
    private CancellationTokenSource? _download;

    /// <param name="open">Opens a web page in the browser.</param>
    public AppUpdateViewModel(IAppUpdateService updates, ISettingsService settings, INotificationService notifications, ReactiveCommand<string, Unit> open)
    {
        _updates = updates;
        _settings = settings;
        _notifications = notifications;
        _open = open;

        OpenCommand = ReactiveCommand.CreateFromObservable(() => open.Execute(Available!.Url));
        UpdateCommand = ReactiveCommand.CreateFromTask(UpdateAsync);
        RestartCommand = ReactiveCommand.Create(() =>
        {
            _updates.RestartAfterExit();
            _restartRequested.OnNext(Unit.Default);
        });
        CheckNowCommand = ReactiveCommand.CreateFromTask(() => CheckAsync(manual: true));
        DismissCommand = ReactiveCommand.Create(Dismiss);
        settings.SettingsChanged
            .Select(s => s.CheckForAppUpdates)
            .DistinctUntilChanged()
            .Where(on => !on)
            .Subscribe(_ =>
            {
                if (State == AppUpdateState.Available)
                    Show(AppUpdateState.None);
            })
            .DisposeWith(Disposables);
    }

    /// <summary>The newer release, while there's something to show about it.</summary>
    [Reactive] public AppRelease? Available { get; private set; }

    [Reactive] public AppUpdateState State { get; private set; }

    /// <summary>The version installed in place of this one, which the next start runs.</summary>
    [Reactive] public AppRelease? Installed { get; private set; }

    /// <summary>"Version 0.2.0 is out", "Updating to 0.2.0", "Version 0.2.0 is ready".</summary>
    [Reactive] public string Label { get; private set; } = "";

    /// <summary>Update installs it here, rather than opening the release page to download it.</summary>
    [Reactive] public bool CanInstall { get; private set; }

    public string UpdateTip => CanInstall
        ? "Download it in the background and install it in place of this version. Your data carries over."
        : "Open the release on GitHub, to download it from there. Your data carries over.";

    public string DismissTip => State switch
    {
        AppUpdateState.Downloading => "Stop downloading",
        AppUpdateState.Ready => "Hide this: the new version starts next time",
        _ => "Don't mention this version again",
    };

    /// <summary>How much of the download has arrived, 0–100.</summary>
    [Reactive] public double Percent { get; private set; }

    [Reactive] public string PercentText { get; private set; } = "";

    public bool IsShown => State != AppUpdateState.None;
    public bool IsAvailable => State == AppUpdateState.Available;
    public bool IsDownloading => State == AppUpdateState.Downloading;
    public bool IsReady => State == AppUpdateState.Ready;

    /// <summary>The release's page, with what's new and the download.</summary>
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    /// <summary>Install it here, in the background, or where that can't be done, open the release page.</summary>
    public ReactiveCommand<Unit, Unit> UpdateCommand { get; }

    /// <summary>Close the app and start the version just installed.</summary>
    public ReactiveCommand<Unit, Unit> RestartCommand { get; }

    /// <summary>Skip the version out, stop its download, or put away the word that it's ready.</summary>
    public ReactiveCommand<Unit, Unit> DismissCommand { get; }

    /// <summary>Settings → Data's "Check now": says how it went, and shows a version that was dismissed.</summary>
    public ReactiveCommand<Unit, Unit> CheckNowCommand { get; }

    /// <summary>Restart now was pressed: the window is to close, and the new version starts once it has.</summary>
    public IObservable<Unit> RestartRequested => _restartRequested.AsObservable();

    /// <summary>The version running: "0.2.0", or null for a build made outside the release workflow.</summary>
    public Version? Current => _updates.Current;

    /// <summary>
    /// The window is up: tidy away what the last update left, say so if this is the first start since one,
    /// and check for a newer version.
    /// </summary>
    public Task OnStartupAsync()
    {
        _ = _updates.CleanUpAsync();
        if (Current?.ToString() is { } running && _settings.Current.LastRunVersion != running)
        {
            if (_settings.Current.LastRunVersion is not null)
                _notifications.ShowSuccess($"Updated to version {running}.", _toastTime);
            _settings.Update(s => s.LastRunVersion = running);
        }
        return CheckAsync();
    }

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
        if (!manual && !_settings.Current.CheckForAppUpdates || State is AppUpdateState.Downloading or AppUpdateState.Ready)
            return;
        var latest = await _updates.LatestAsync();
        if (latest is null)
        {
            if (manual)
                _notifications.ShowError("Couldn't reach GitHub to check for a newer version. Try again later.", _toastTime);
            return;
        }
        _settings.Update(s => s.AppUpdateCheckedAt = DateTimeOffset.Now);
        var newer = Version.TryParse(latest.Version, out var version) && version > current;
        if (newer && (manual || latest.Version != _settings.Current.SkippedAppVersion && _settings.Current.CheckForAppUpdates))
        {
            Available = latest;
            CanInstall = _updates.CanInstall(latest);
            Show(AppUpdateState.Available);
        }
        else if (manual)
        {
            _notifications.ShowSuccess($"You have the newest version, {current}.", _toastTime);
        }
    }

    private async Task UpdateAsync()
    {
        var release = Available!;
        if (!CanInstall)
        {
            await _open.Execute(release.Url);
            return;
        }

        using var download = _download = new CancellationTokenSource();
        Percent = 0;
        PercentText = "";
        Show(AppUpdateState.Downloading);
        try
        {
            await _updates.InstallAsync(release, new Progress<DownloadProgress>(Downloaded), download.Token);
            Installed = release;
            Show(AppUpdateState.Ready);
            _notifications.ShowSuccess($"Version {release.Version} is installed: restart to use it, or it starts next time.", _toastTime);
        }
        catch (OperationCanceledException)
        {
            Show(AppUpdateState.Available);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidDataException)
        {
            Show(AppUpdateState.Available);
            _notifications.ShowError($"Couldn't download version {release.Version}: {ex.Message} Try again, or get it from the release page.", _toastTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The exe's folder can't be written to, such as Program Files: the download from the page still works.
            CanInstall = false;
            Show(AppUpdateState.Available);
            _notifications.ShowError($"Couldn't install version {release.Version} beside this one ({ex.Message}). "
                                     + "Update opens the release page instead, to download it from there.", _toastTime);
        }
        finally
        {
            _download = null;
        }
    }

    private void Downloaded(DownloadProgress progress)
    {
        var total = progress.Total > 0 ? progress.Total : Available?.Exe?.Size ?? 0;
        if (total <= 0)
            return;
        var percent = Math.Min(100, Math.Floor(progress.Done * 100.0 / total));
        if (percent == Percent && PercentText.Length > 0)
            return;
        Percent = percent;
        PercentText = $"{percent:0}%";
    }

    private void Dismiss()
    {
        switch (State)
        {
            case AppUpdateState.Available:
                _settings.Update(s => s.SkippedAppVersion = Available!.Version);
                Show(AppUpdateState.None);
                break;
            case AppUpdateState.Downloading:
                _download?.Cancel();
                break;
            case AppUpdateState.Ready:
                // Installed already: it starts next time, so there's nothing more to say about it.
                Show(AppUpdateState.None);
                break;
        }
    }

    private void Show(AppUpdateState state)
    {
        State = state;
        if (state == AppUpdateState.None)
            Available = null;
        Label = (state, Available?.Version) switch
        {
            (AppUpdateState.Available, { } version) => $"Version {version} is out",
            (AppUpdateState.Downloading, { } version) => $"Updating to {version}",
            (AppUpdateState.Ready, { } version) => $"Version {version} is ready",
            _ => "",
        };
        this.RaisePropertyChanged(nameof(IsShown));
        this.RaisePropertyChanged(nameof(IsAvailable));
        this.RaisePropertyChanged(nameof(DismissTip));
        this.RaisePropertyChanged(nameof(UpdateTip));
        this.RaisePropertyChanged(nameof(IsDownloading));
        this.RaisePropertyChanged(nameof(IsReady));
    }
}
