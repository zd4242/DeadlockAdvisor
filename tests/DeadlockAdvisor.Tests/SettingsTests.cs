using System.Text.Json.Nodes;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

public class SettingsTests
{
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
            s.DataRoot = @"D:\Dev\Python\deadlock_advisor";
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
        Assert.Equal(@"D:\Dev\Python\deadlock_advisor", settings.DataRoot);
        Assert.Equal(5, settings.ZoomIndex);
        Assert.Equal(60, settings.ResultsMinPercent);
        Assert.True(settings.ResultsByTier);
        Assert.Equal(71.5, settings.VisionGeometry["2560x1440"]["pitch"]!.GetValue<double>());

        var restored = new MatchState();
        restored.LoadSaved(settings.LastMatch, ["zeta", "alpha", "mid"]);
        Assert.Equal(["zeta", "alpha", "mid"], restored.RoleMap.Keys);
        Assert.Equal("alpha", restored.SelfHero);
    }

    private static async Task WaitForFile(string path)
    {
        for (var attempt = 0; attempt < 100 && !File.Exists(path); attempt++)
            await Task.Delay(20);
        Assert.True(File.Exists(path));
    }
}
