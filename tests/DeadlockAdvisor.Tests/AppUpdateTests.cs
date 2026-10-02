using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.MainWindow;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Fakes;
using ReactiveUI;

namespace DeadlockAdvisor.Tests;

/// <summary>Saying when a newer version of the app is out, from GitHub's newest release.</summary>
public sealed class AppUpdateTests : IDisposable
{
    private const string ReleasePage = "https://github.com/zd4242/DeadlockAdvisor/releases/tag/v0.2.0";

    private readonly FakeDeadlockApi _api = new();
    private readonly FakeSettingsService _settings = new();
    private readonly List<string> _opened = [];
    private readonly ReactiveCommand<string, Unit> _open;

    public AppUpdateTests()
    {
        _open = ReactiveCommand.Create<string>(_opened.Add);
    }

    public void Dispose() => _open.Dispose();

    private void Release(string tag) =>
        _api.Bytes[AppUpdateService.LatestUrl] = Encoding.UTF8.GetBytes(
            $$"""{"tag_name": "{{tag}}", "html_url": "https://github.com/zd4242/DeadlockAdvisor/releases/tag/{{tag}}", "prerelease": false}""");

    private AppUpdateViewModel Check(string? running, out Task checking)
    {
        var vm = new AppUpdateViewModel(new AppUpdateService(_api, running is null ? null : Version.Parse(running)), _settings, _open);
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
}
