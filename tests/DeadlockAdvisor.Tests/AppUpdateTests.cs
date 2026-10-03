using System.Reactive;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.MainWindow;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;
using ReactiveUI;

namespace DeadlockAdvisor.Tests;

/// <summary>Saying when a newer version of the app is out, from GitHub's newest release.</summary>
public sealed class AppUpdateTests : IDisposable
{
    private const string ReleasePage = "https://github.com/zd4242/DeadlockAdvisor/releases/tag/v0.2.0";
    private const string ExeUrl = "https://github.com/zd4242/DeadlockAdvisor/releases/download/v0.2.0/DeadlockAdvisor.exe";

    private static readonly byte[] _newExe = "the new version"u8.ToArray();
    private static readonly byte[] _oldExe = "the version running"u8.ToArray();

    private readonly FakeDeadlockApi _api = new();
    private readonly FakeSettingsService _settings = new();
    private readonly List<string> _opened = [];
    private readonly NotificationService _notifications = new(new FakeLoggingService());
    private readonly List<Services.Contracts.Notification> _toasts = [];
    private readonly ReactiveCommand<string, Unit> _open;

    public AppUpdateTests()
    {
        _open = ReactiveCommand.Create<string>(_opened.Add);
        _notifications.Notifications.Subscribe(_toasts.Add);
    }

    public void Dispose() => _open.Dispose();

    /// <summary>A release as GitHub's API gives it, with the Windows exe and its hash when <paramref name="exe"/> is given.</summary>
    private void Release(string tag, byte[]? exe = null)
    {
        var assets = exe is null
            ? ""
            : $$"""
                [{"name": "DeadlockAdvisor.exe", "size": {{exe.Length}}, "digest": "sha256:{{Convert.ToHexStringLower(SHA256.HashData(exe))}}",
                  "browser_download_url": "{{ExeUrl}}"}]
                """;
        _api.Bytes[AppUpdateService.LatestUrl] = Encoding.UTF8.GetBytes(
            $$"""{"tag_name": "{{tag}}", "html_url": "https://github.com/zd4242/DeadlockAdvisor/releases/tag/{{tag}}", "prerelease": false, "assets": [{{assets.Trim().Trim('[', ']')}}]}""");
        if (exe is not null)
            _api.Bytes[ExeUrl] = exe;
    }

    /// <summary>A running exe, in a folder of its own, for an update to replace.</summary>
    private static string RunningExe(TempDirectory folder)
    {
        var exe = Path.Combine(folder.Path, "DeadlockAdvisor (1).exe");
        File.WriteAllBytes(exe, _oldExe);
        return exe;
    }

    private AppUpdateViewModel Check(string? running, out Task checking)
    {
        var vm = new AppUpdateViewModel(new AppUpdateService(_api, running is null ? null : Version.Parse(running)), _settings, _notifications, _open);
        checking = vm.CheckAsync();
        return vm;
    }

    [Theory]
    [InlineData("0.1.1+15b6f95dbeb7522dd8e2318fd25fffae19d64634", "0.1.1")]
    [InlineData("0.0.0-dev+15b6f95dbeb7522dd8e2318fd25fffae19d64634", null)]
    [InlineData("0.0.0-dev.12", null)]
    [InlineData(null, null)]
    public void OnlyAReleaseBuildHasAVersionToCompare(string? informational, string? release) =>
        Assert.Equal(release, AppVersion.ReleaseOf(informational)?.ToString());

    [Fact]
    public async Task TheNewestReleaseComesFromItsTagAndPage()
    {
        var service = new AppUpdateService(_api, new Version(0, 1, 1));
        Assert.Null(await service.LatestAsync());

        Release("v0.2.0");
        Assert.Equal(new AppRelease("0.2.0", ReleasePage), await service.LatestAsync());

        _api.Bytes[AppUpdateService.LatestUrl] = "{\"message\": \"API rate limit exceeded\"}"u8.ToArray();
        Assert.Null(await service.LatestAsync());
    }

    [Fact]
    public async Task ANewerReleaseShowsAndOpensItsPage()
    {
        Release("v0.2.0");

        using var vm = Check("0.1.1", out var checking);
        await checking;

        Assert.Equal("Version 0.2.0 is out", vm.Label);
        await vm.OpenCommand.Execute();
        Assert.Equal([ReleasePage], _opened);
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("0.3.0")]
    public async Task TheSameOrAnOlderReleaseSaysNothing(string running)
    {
        Release("v0.2.0");

        using var vm = Check(running, out var checking);
        await checking;

        Assert.Null(vm.Available);
    }

    [Fact]
    public async Task ABuildFromOutsideTheReleaseWorkflowOrWithTheCheckOffDoesntAsk()
    {
        Release("v0.2.0");

        using (var dev = Check(null, out var checking))
            await checking;
        _settings.Current.CheckForAppUpdates = false;
        using (var off = Check("0.1.1", out var checking))
            await checking;

        Assert.Empty(_api.Asked);
    }

    [Fact]
    public async Task DismissingSkipsThatVersionButNotTheNext()
    {
        Release("v0.2.0");
        using var vm = Check("0.1.1", out var checking);
        await checking;

        await vm.DismissCommand.Execute();

        Assert.Null(vm.Available);
        Assert.Equal("0.2.0", _settings.Current.SkippedAppVersion);
        await vm.CheckAsync();
        Assert.Null(vm.Available);
        // Asked for, it's shown again.
        await vm.CheckNowCommand.Execute();
        Assert.Equal("0.2.0", vm.Available!.Version);
        Release("v0.2.1");
        await vm.CheckAsync();
        Assert.Equal("0.2.1", vm.Available!.Version);
    }

    [Fact]
    public async Task TurningTheCheckOffHidesIt()
    {
        Release("v0.2.0");
        using var vm = Check("0.1.1", out var checking);
        await checking;

        _settings.Update(s => s.CheckForAppUpdates = false);

        Assert.Null(vm.Available);
    }

    [Fact]
    public async Task CheckingByHandSaysHowItWentAndWhen()
    {
        using var vm = Check("0.2.0", out var checking);
        await checking;
        Assert.Empty(_toasts);
        Assert.Null(_settings.Current.AppUpdateCheckedAt);

        await vm.CheckNowCommand.Execute();
        Assert.StartsWith("Couldn't reach GitHub", _toasts[^1].Message);

        Release("v0.2.0");
        await vm.CheckNowCommand.Execute();
        Assert.Equal("You have the newest version, 0.2.0.", _toasts[^1].Message);
        Assert.NotNull(_settings.Current.AppUpdateCheckedAt);

        using var dev = Check(null, out var devChecking);
        await devChecking;
        await dev.CheckNowCommand.Execute();
        Assert.StartsWith("This build wasn't made by the release workflow", _toasts[^1].Message);
    }

    [Fact]
    public async Task TheReleasesExeComesWithItsSizeAndHash()
    {
        Release("v0.2.0", _newExe);

        var latest = await new AppUpdateService(_api, new Version(0, 1, 1)).LatestAsync();

        Assert.Equal(new AppDownload(ExeUrl, _newExe.Length, Convert.ToHexStringLower(SHA256.HashData(_newExe))), latest!.Exe);
    }

    [Fact]
    public async Task UpdatingDownloadsTheExeChecksItAndPutsItInPlaceOfThisOne()
    {
        using var folder = new TempDirectory();
        var exe = RunningExe(folder);
        var service = new AppUpdateService(_api, new Version(0, 1, 1), exe);
        Release("v0.2.0", _newExe);
        using var vm = new AppUpdateViewModel(service, _settings, _notifications, _open);
        await vm.CheckAsync();
        Assert.True(vm.CanInstall);

        await vm.UpdateCommand.Execute();

        Assert.Equal(AppUpdateState.Ready, vm.State);
        Assert.Equal("Version 0.2.0 is ready", vm.Label);
        Assert.Equal("100%", vm.PercentText);
        Assert.Equal(_newExe, File.ReadAllBytes(exe));
        Assert.Equal(_oldExe, File.ReadAllBytes(AppUpdateService.OldPath(exe)));
        Assert.False(File.Exists(AppUpdateService.NewPath(exe)));
        Assert.StartsWith("Version 0.2.0 is installed", _toasts[^1].Message);
        Assert.Empty(_opened);

        var restarts = 0;
        using var _ = vm.RestartRequested.Subscribe(_ => restarts++);
        await vm.RestartCommand.Execute();
        Assert.Equal(1, restarts);
        service.RestartAfterExit(false);

        // The next start tidies away the exe it replaced.
        await service.CleanUpAsync();
        Assert.False(File.Exists(AppUpdateService.OldPath(exe)));
    }

    [Fact]
    public async Task ADamagedDownloadLeavesThisVersionAsItWas()
    {
        using var folder = new TempDirectory();
        var exe = RunningExe(folder);
        Release("v0.2.0", _newExe);
        _api.Bytes[ExeUrl] = "not the release"u8.ToArray();
        using var vm = new AppUpdateViewModel(new AppUpdateService(_api, new Version(0, 1, 1), exe), _settings, _notifications, _open);
        await vm.CheckAsync();

        await vm.UpdateCommand.Execute();

        Assert.Equal(AppUpdateState.Available, vm.State);
        Assert.Equal(_oldExe, File.ReadAllBytes(exe));
        Assert.Equal([Path.GetFileName(exe)], Directory.GetFiles(folder.Path).Select(Path.GetFileName));
        Assert.Contains("didn't arrive intact", _toasts[^1].Message);
    }

    [Fact]
    public async Task WithoutAnExeToReplaceUpdateOpensTheReleasePage()
    {
        Release("v0.2.0", _newExe);
        using var vm = Check("0.1.1", out var checking);
        await checking;

        Assert.False(vm.CanInstall);
        await vm.UpdateCommand.Execute();

        Assert.Equal([ReleasePage], _opened);
        Assert.Equal(AppUpdateState.Available, vm.State);
    }

    [Fact]
    public async Task TheFirstStartAfterAnUpdateSaysSo()
    {
        using (var first = new AppUpdateViewModel(new AppUpdateService(_api, new Version(0, 1, 1)), _settings, _notifications, _open))
            await first.OnStartupAsync();
        Assert.Empty(_toasts);
        Assert.Equal("0.1.1", _settings.Current.LastRunVersion);

        using (var updated = new AppUpdateViewModel(new AppUpdateService(_api, new Version(0, 2, 0)), _settings, _notifications, _open))
            await updated.OnStartupAsync();
        Assert.Equal("Updated to version 0.2.0.", Assert.Single(_toasts).Message);

        using (var again = new AppUpdateViewModel(new AppUpdateService(_api, new Version(0, 2, 0)), _settings, _notifications, _open))
            await again.OnStartupAsync();
        Assert.Single(_toasts);
    }
}