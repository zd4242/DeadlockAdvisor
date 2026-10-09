using System.Text.Json.Nodes;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
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
        Assert.True(settings.SoundOnDetect);
        Assert.True(settings.MinimizeToDetect);
        Assert.True(settings.KeepUnreadCaptures);
        Assert.True(settings.RememberCorrections);
    }

    [Fact]
    public async Task AnUnreadableSettingsFileIsKeptAndTheUserIsTold()
    {
        using var folder = new TempDirectory();
        await File.WriteAllTextAsync(folder.File("settings.json"), """{ "DataRoot": "D:\\Games", """);
        var notes = new List<Notification>();
        var notifications = new NotificationService(new FakeLoggingService());
        using var _ = notifications.Notifications.Subscribe(notes.Add);

        var service = new JsonSettingsService(new FakeLoggingService(), folder.Path, notifications);
        await service.LoadAsync();

        Assert.Equal(new AppSettings().ZoomIndex, service.Current.ZoomIndex);
        var kept = Assert.Single(Directory.GetFiles(folder.Path, "settings.bad-*.json"));
        Assert.Equal("""{ "DataRoot": "D:\\Games", """, await File.ReadAllTextAsync(kept));
        var note = Assert.Single(notes);
        Assert.Equal(NotificationSeverity.Warning, note.Severity);
        Assert.Contains(Path.GetFileName(kept), note.Message);

        service.Update(s => s.ZoomIndex = 3);
        await WaitForText(folder.File("settings.json"), "\"ZoomIndex\": 3");
        Assert.Equal("""{ "DataRoot": "D:\\Games", """, await File.ReadAllTextAsync(kept));
    }

    [Fact]
    public async Task OnlyTheNewestThreeUnreadableCopiesAreKept()
    {
        using var folder = new TempDirectory();
        for (var day = 1; day <= 4; day++)
            await File.WriteAllTextAsync(folder.File($"settings.bad-2026010{day}-120000.json"), "old");
        await File.WriteAllTextAsync(folder.File("settings.json"), "not json");

        await new JsonSettingsService(new FakeLoggingService(), folder.Path).LoadAsync();

        var names = Directory.GetFiles(folder.Path, "settings.bad-*.json").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(3, names.Count);
        Assert.DoesNotContain("settings.bad-20260101-120000.json", names);
        Assert.DoesNotContain("settings.bad-20260102-120000.json", names);
    }

    [Fact]
    public async Task ASettingsFileThatReadsFineLeavesNoCopy()
    {
        using var folder = new TempDirectory();
        await File.WriteAllTextAsync(folder.File("settings.json"), """{ "ZoomIndex": 4 }""");

        await new JsonSettingsService(new FakeLoggingService(), folder.Path).LoadAsync();

        Assert.Empty(Directory.GetFiles(folder.Path, "settings.bad-*.json"));
    }

    private static async Task WaitForText(string path, string text)
    {
        for (var attempt = 0; attempt < 100 && !(File.Exists(path) && (await ReadShared(path)).Contains(text)); attempt++)
            await Task.Delay(20);
        Assert.Contains(text, await ReadShared(path));
    }

    private static async Task<string> ReadShared(string path)
    {
        try
        {
            return await File.ReadAllTextAsync(path);
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static async Task WaitForFile(string path)
    {
        for (var attempt = 0; attempt < 100 && !File.Exists(path); attempt++)
            await Task.Delay(20);
        Assert.True(File.Exists(path));
    }
}
