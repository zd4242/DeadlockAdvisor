using System.Text.Json.Nodes;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

public class SettingsTests
{
    [Theory]
    [InlineData("1.2.0+15b6f95dbeb7522dd8e2318fd25fffae19d64634", "1.2.0 (15b6f95)")]
    [InlineData("0.0.0-dev+abc", "0.0.0-dev (abc)")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData(null, "unknown")]
    public void TheVersionShowsTheReleaseAndTheCommitItWasBuiltFrom(string? informational, string shown) =>
        Assert.Equal(shown, Core.AppVersion.Describe(informational));

    /// <summary>The commit is missing when it's built from a copy of the source without git.</summary>
    [Fact]
    public void ThisBuildIsALocalOne() =>
        Assert.Matches(@"^0\.0\.0-dev( \([0-9a-f]{7}\))?$", Core.AppVersion.Text);

    [Fact]
    public async Task SettingsRoundTripIncludingTheSavedMatchInOrder()
    {
        using var folder = new TempDirectory();
        var match = new MatchState();
        match.SetRole("zeta", Role.Enemy);
        match.SetRole("alpha", Role.Self);
        match.SetRole("mid", Role.Ally);

        var service = new JsonSettingsService(new FakeLoggingService(), folder.Path);
        service.Update(s =>
        {
            s.DataRoot = @"C:\Games\DeadlockAdvisor";
            s.ZoomIndex = 5;
            s.LastMatch = match.ToSaved();
            s.ResultsMinPercent = 60;
            s.ResultsByTier = true;
            s.VisionGeometry["2560x1440"] = new JsonObject { ["version"] = 3, ["pitch"] = 71.5 };
        });
        await WaitForFile(folder.File("settings.json"));

        var reloaded = new JsonSettingsService(new FakeLoggingService(), folder.Path);
        await reloaded.LoadAsync();

        var settings = reloaded.Current;
        Assert.Equal(@"C:\Games\DeadlockAdvisor", settings.DataRoot);
        Assert.Equal(5, settings.ZoomIndex);
        Assert.Equal(60, settings.ResultsMinPercent);
        Assert.True(settings.ResultsByTier);
        Assert.Equal(71.5, settings.VisionGeometry["2560x1440"]["pitch"]!.GetValue<double>());

        var restored = new MatchState();
        restored.LoadSaved(settings.LastMatch, ["zeta", "alpha", "mid"]);
        Assert.Equal(["zeta", "alpha", "mid"], restored.RoleMap.Keys);
        Assert.Equal("alpha", restored.SelfHero);
    }

    /// <summary>A settings file from before the Settings page leaves every preference at what the app used to do.</summary>
    [Fact]
    public async Task PreferencesMissingFromAnOlderFileKeepTheirDefaults()
    {
        using var folder = new TempDirectory();
        await File.WriteAllTextAsync(folder.File("settings.json"), """{ "ZoomIndex": 4 }""");

        var service = new JsonSettingsService(new FakeLoggingService(), folder.Path);
        await service.LoadAsync();

        var settings = service.Current;
        Assert.Equal(4, settings.ZoomIndex);
        Assert.True(settings.ReopenLastPage);
        Assert.True(settings.ReopenLastMatch);
        Assert.True(settings.ShowRandomButtons);
        Assert.True(settings.ShowExplainMath);
        Assert.False(settings.ShowModelEditors);
        Assert.True(settings.CheckForNewerPatch);
        Assert.True(settings.DetectFromAnywhere);
        Assert.False(settings.ComeUpForReview);
        Assert.True(settings.MinimizeToDetect);
        Assert.True(settings.KeepUnreadCaptures);
        Assert.True(settings.RememberCorrections);
    }

    private static async Task WaitForFile(string path)
    {
        for (var attempt = 0; attempt < 100 && !File.Exists(path); attempt++)
            await Task.Delay(20);
        Assert.True(File.Exists(path));
    }
}
