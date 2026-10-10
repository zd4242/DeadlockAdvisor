using System.Text.Json.Nodes;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Settings.Data;
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

    [Theory]
    [InlineData(true, true, UpdateMode.Automatic)]
    [InlineData(true, false, UpdateMode.Automatic)]
    [InlineData(false, true, UpdateMode.TellMe)]
    [InlineData(false, false, UpdateMode.Off)]
    public void EveryCombinationOfTheUpdateFlagsReadsAsOneMode(bool automatic, bool tellMe, UpdateMode mode)
    {
        var settings = new AppSettings
        {
            AutoUpdateMatchData = automatic, CheckForNewerPatch = tellMe, AutoUpdateModel = automatic, CheckForNewHeroes = tellMe,
        };

        Assert.Equal(mode, UpdateModes.MatchData(settings));
        Assert.Equal(mode, UpdateModes.Formulas(settings));
    }

    [Fact]
    public void ChoosingAModeSetsItsFlagsAndLeavesTheRestAlone()
    {
        foreach (var automatic in new[] { true, false })
        foreach (var tellMe in new[] { true, false })
        foreach (var mode in Enum.GetValues<UpdateMode>())
        {
            var settings = new AppSettings
            {
                AutoUpdateMatchData = automatic, CheckForNewerPatch = tellMe, AutoUpdateModel = automatic, CheckForNewHeroes = tellMe,
                CheckForAppUpdates = tellMe,
            };

            UpdateModes.SetMatchData(settings, mode);

            Assert.Equal(mode, UpdateModes.MatchData(settings));
            Assert.Equal((automatic, tellMe), (settings.AutoUpdateModel, settings.CheckForNewHeroes));
            Assert.Equal(tellMe, settings.CheckForAppUpdates);
            // Automatic only matters while it's on, so the check-only flag stays as it was.
            if (mode == UpdateMode.Automatic)
                Assert.Equal(tellMe, settings.CheckForNewerPatch);

            settings.AutoUpdateMatchData = automatic;
            settings.CheckForNewerPatch = tellMe;
            UpdateModes.SetFormulas(settings, mode);

            Assert.Equal(mode, UpdateModes.Formulas(settings));
            Assert.Equal((automatic, tellMe), (settings.AutoUpdateMatchData, settings.CheckForNewerPatch));
        }
    }

    [Fact]
    public void TheAppIsOnlyEverToldAbout()
    {
        var settings = new AppSettings();
        Assert.Equal(UpdateMode.TellMe, UpdateModes.App(settings));

        UpdateModes.SetApp(settings, UpdateMode.Off);
        Assert.False(settings.CheckForAppUpdates);
        Assert.Equal(UpdateMode.Off, UpdateModes.App(settings));

        UpdateModes.SetApp(settings, UpdateMode.TellMe);
        Assert.True(settings.CheckForAppUpdates);
    }

    /// <summary>The modes are a view over the old flags, so a file from the previous version shows the mode it meant, and one written now reads the same there.</summary>
    [Fact]
    public async Task AnOlderSettingsFileShowsTheModesItMeantAndAModeIsSavedAsTheOldFlags()
    {
        using var folder = new TempDirectory();
        await File.WriteAllTextAsync(folder.File("settings.json"),
            """{ "AutoUpdateMatchData": false, "CheckForNewerPatch": true, "AutoUpdateModel": false, "CheckForNewHeroes": false }""");
        var service = new JsonSettingsService(new FakeLoggingService(), folder.Path);
        await service.LoadAsync();

        Assert.Equal(UpdateMode.TellMe, UpdateModes.MatchData(service.Current));
        Assert.Equal(UpdateMode.Off, UpdateModes.Formulas(service.Current));

        service.Update(s => UpdateModes.SetFormulas(s, UpdateMode.TellMe));
        await WaitForText(folder.File("settings.json"), "\"CheckForNewHeroes\": true");
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(folder.File("settings.json")))!;
        Assert.False(saved["AutoUpdateModel"]!.GetValue<bool>());
        Assert.True(saved["CheckForNewHeroes"]!.GetValue<bool>());
        Assert.False(saved["AutoUpdateMatchData"]!.GetValue<bool>());
        Assert.True(saved["CheckForNewerPatch"]!.GetValue<bool>());
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
        for (var attempt = 0; attempt < 500 && !(File.Exists(path) && (await ReadShared(path)).Contains(text)); attempt++)
            await Task.Delay(20);
        Assert.Contains(text, await ReadShared(path));
    }

    /// <summary>
    /// Reads without keeping the file from being replaced: the settings service swaps a temp file over it once and
    /// doesn't retry, so a reader holding it open at that moment would lose the write.
    /// </summary>
    private static async Task<string> ReadShared(string path)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
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
