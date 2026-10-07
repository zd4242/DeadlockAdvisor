using System.Text;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.GameApi;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Tests.Ui;

namespace DeadlockAdvisor.Tests;

/// <summary>The published model (the seed data's model.json) and taking a newer one into a data folder.</summary>
public sealed class ModelUpdateTests : IDisposable
{
    private static string SeedDir => Path.Combine(UiHarness.RepoRoot(), "src", "Assets", "SeedData");

    private readonly TempDirectory _data = new();
    private readonly FakeDeadlockApi _api = new();
    private readonly ModelUpdateService _service;

    public ModelUpdateTests()
    {
        DataService.SeedIfEmpty(_data.Path);
        _service = new ModelUpdateService(_api);
    }

    public void Dispose() => _data.Dispose();

    private string DataFile(string file) => Path.Combine(_data.Path, file);

    private static byte[] Seed(string file) => File.ReadAllBytes(Path.Combine(SeedDir, file));

    /// <summary>A CSV with its first row's last field set to <paramref name="value"/>: a real edit, still loadable.</summary>
    public static byte[] FirstRowEnding(byte[] csv, string value)
    {
        var lines = Encoding.UTF8.GetString(csv).Split("\r\n");
        lines[1] = lines[1][..(lines[1].LastIndexOf(',') + 1)] + value;
        return Encoding.UTF8.GetBytes(string.Join("\r\n", lines));
    }

    /// <summary>The seed published as a newer model with some files changed, and served by the fake API.</summary>
    private ModelManifest Publish(params (string File, byte[] Bytes)[] changed) => PublishWithNotes([], changed);

    private ModelManifest PublishWithNotes(IReadOnlyList<ModelNote> notes, params (string File, byte[] Bytes)[] changed)
    {
        var contents = ModelManifest.ModelFiles.ToDictionary(file => file,
            file => changed.Any(change => change.File == file) ? changed.First(change => change.File == file).Bytes : Seed(file));
        var published = new ModelManifest(ModelManifest.CurrentFormat, "2026-10-09",
            contents.ToDictionary(pair => pair.Key, pair => ModelManifest.Hash(pair.Value)), new Dictionary<string, string>()) { Notes = notes };
        foreach (var (file, bytes) in contents)
            _api.Bytes[published.UrlOf(file)] = bytes;
        _api.Bytes[ModelManifest.ManifestUrl] = published.ToJsonBytes();
        return published;
    }

    private async Task<IReadOnlySet<string>> InstallAsync(ModelUpdatePlan plan, params string[] replace) =>
        ModelUpdateService.Install(_data.Path, plan, await _service.DownloadAsync(plan.Published, plan.Quiet.Concat(plan.Edited.Where(replace.Contains))));

    [Fact]
    public void TheSeedsModelJsonListsEveryModelFileByItsHash()
    {
        var path = Path.Combine(SeedDir, ModelManifest.FileName);
        var listed = File.Exists(path) ? ModelManifest.Parse(File.ReadAllBytes(path)) : null;
        var actual = listed is null ? null : ModelPublisher.Listing(SeedDir, listed);
        if (Golden.Updating && (actual is null || !actual.SequenceEqual(File.ReadAllBytes(path))))
        {
            // Changed files are a new version: published today.
            var today = ModelManifest.Of(SeedDir, DateTime.UtcNow.ToString("yyyy-MM-dd")) with { Notes = listed?.Notes ?? [] };
            File.WriteAllBytes(path, today.ToJsonBytes());
            return;
        }

        Assert.True(listed is not null, $"Regenerate {path} with DEADLOCK_UPDATE_GOLDENS=1.");
        Assert.Equal(ModelManifest.ModelFiles, listed.Files.Keys);
        Assert.True(actual!.SequenceEqual(File.ReadAllBytes(path)),
            "The seed's files changed: regenerate model.json with DEADLOCK_UPDATE_GOLDENS=1, which publishes them as a new version once pushed.");
    }

    /// <summary>A copy of the seed to publish into, without the notes it has published so far: they come and go with the seed.</summary>
    private static TempDirectory SeedCopy()
    {
        var seed = new TempDirectory();
        foreach (var file in Directory.GetFiles(SeedDir))
            File.Copy(file, Path.Combine(seed.Path, Path.GetFileName(file)));
        var manifest = Path.Combine(seed.Path, ModelManifest.FileName);
        File.WriteAllBytes(manifest, (ModelManifest.Parse(File.ReadAllBytes(manifest)) with { Notes = [] }).ToJsonBytes());
        return seed;
    }

    [Fact]
    public void PublishingCopiesTheChangedFilesIntoTheSeedAndDatesItsModelJson()
    {
        using var seed = SeedCopy();
        var weights = FirstRowEnding(Seed(DataStore.TraitWeightsFile), "1.3");
        File.WriteAllBytes(DataFile(DataStore.TraitWeightsFile), weights);

        var result = ModelPublisher.Publish(_data.Path, seed.Path, "2026-10-09");

        Assert.Equal([DataStore.TraitWeightsFile], result.Files);
        Assert.Equal(weights, File.ReadAllBytes(Path.Combine(seed.Path, DataStore.TraitWeightsFile)));
        var listed = ModelManifest.Parse(File.ReadAllBytes(Path.Combine(seed.Path, ModelManifest.FileName)));
        Assert.Equal(ModelManifest.Of(seed.Path, "2026-10-09").ToJsonBytes(), listed.ToJsonBytes());
        // An install of the seed before takes just that file, without asking.
        using var install = new TempDirectory();
        DataService.SeedIfEmpty(install.Path);
        var plan = ModelUpdatePlan.For(listed, install.Path, askAgain: false);
        Assert.Equal([DataStore.TraitWeightsFile], plan.Quiet);
        Assert.Empty(plan.Edited);

        Assert.False(ModelPublisher.Publish(_data.Path, seed.Path, "2026-10-10").Changed);
        Assert.Equal("2026-10-09", ModelManifest.Parse(File.ReadAllBytes(Path.Combine(seed.Path, ModelManifest.FileName))).Published);
    }

    [Fact]
    public void PublishingLeavesOutMatchDataThatLeansTowardSomeRanksAndRefusesAFolderThatDoesntLoad()
    {
        using var seed = SeedCopy();
        var meta = DataFile(DataStore.MatchMetaFile);
        File.WriteAllText(meta, File.ReadAllText(meta).Replace("\"rank\": \"all\"", "\"rank\": {\"min\": 7, \"max\": 11}"));
        File.WriteAllText(DataFile(DataStore.MatchLiftFile), File.ReadAllText(DataFile(DataStore.MatchLiftFile)) + "\r\n");

        var result = ModelPublisher.Publish(_data.Path, seed.Path, "2026-10-09");

        Assert.False(result.MatchData);
        Assert.NotNull(result.MatchDataSkipped);
        Assert.Equal(Seed(DataStore.MatchLiftFile), File.ReadAllBytes(Path.Combine(seed.Path, DataStore.MatchLiftFile)));

        File.Delete(DataFile(DataStore.HeroesFile));
        Assert.Throws<InvalidOperationException>(() => ModelPublisher.Publish(_data.Path, seed.Path, "2026-10-09"));
    }

    [Fact]
    public async Task AFreshInstallIsUpToDateWithTheModelItCameWith()
    {
        var published = Publish();

        var fetched = await _service.PublishedAsync();
        Assert.Equal(published.ToJsonBytes(), fetched!.ToJsonBytes());
        var plan = ModelUpdatePlan.For(published, _data.Path, askAgain: true);

        Assert.False(plan.HasWork);
        Assert.NotNull(plan.Installed);
    }

    [Fact]
    public async Task FilesUnchangedHereAreReplacedQuietlyKeepingABackup()
    {
        var weights = FirstRowEnding(Seed(DataStore.TraitWeightsFile), "1.3");
        var published = Publish((DataStore.TraitWeightsFile, weights));

        var plan = ModelUpdatePlan.For(published, _data.Path, askAgain: false);
        Assert.Equal([DataStore.TraitWeightsFile], plan.Quiet);
        Assert.Empty(plan.Edited);
        var written = await InstallAsync(plan);

        Assert.Equal([DataStore.TraitWeightsFile], written);
        Assert.Equal(weights, File.ReadAllBytes(DataFile(DataStore.TraitWeightsFile)));
        Assert.Single(Directory.GetFiles(Path.Combine(_data.Path, BackedUpFile.BackupFolderName), "trait_weights.*.csv"));
        Assert.Equal(published.Files, ModelManifest.Installed(_data.Path)!.Files);
        Assert.Equal("2026-10-09", ModelManifest.Installed(_data.Path)!.Published);
        Assert.False(ModelUpdatePlan.For(published, _data.Path, askAgain: true).HasWork);
    }

    [Fact]
    public async Task FilesChangedHereAreAskedAboutAndKeptUnlessReplaced()
    {
        var mine = FirstRowEnding(Seed(DataStore.HeroScoresFile), "1");
        File.WriteAllBytes(DataFile(DataStore.HeroScoresFile), mine);
        var theirs = FirstRowEnding(Seed(DataStore.HeroScoresFile), "2");
        var weights = FirstRowEnding(Seed(DataStore.TraitWeightsFile), "1.3");
        var published = Publish((DataStore.HeroScoresFile, theirs), (DataStore.TraitWeightsFile, weights));

        var plan = ModelUpdatePlan.For(published, _data.Path, askAgain: false);
        Assert.Equal([DataStore.HeroScoresFile], plan.Edited);
        Assert.Equal([DataStore.TraitWeightsFile], plan.Quiet);
        await InstallAsync(plan);

        // Kept: this version doesn't ask again, but a check on demand does.
        Assert.Equal(mine, File.ReadAllBytes(DataFile(DataStore.HeroScoresFile)));
        Assert.Equal(weights, File.ReadAllBytes(DataFile(DataStore.TraitWeightsFile)));
        Assert.False(ModelUpdatePlan.For(published, _data.Path, askAgain: false).HasWork);
        var again = ModelUpdatePlan.For(published, _data.Path, askAgain: true);
        Assert.Equal([DataStore.HeroScoresFile], again.Edited);

        await InstallAsync(again, DataStore.HeroScoresFile);

        Assert.Equal(theirs, File.ReadAllBytes(DataFile(DataStore.HeroScoresFile)));
        Assert.Empty(ModelManifest.Installed(_data.Path)!.Kept);
        Assert.False(ModelUpdatePlan.For(published, _data.Path, askAgain: true).HasWork);
    }

    [Fact]
    public void AFileChangedHereIsLeftAloneUntilANewerVersionIsPublished()
    {
        File.WriteAllBytes(DataFile(DataStore.HeroScoresFile), FirstRowEnding(Seed(DataStore.HeroScoresFile), "1"));

        Assert.False(ModelUpdatePlan.For(Publish(), _data.Path, askAgain: true).HasWork);
    }

    [Fact]
    public async Task ANewerVersionOfAFileKeptAsksAgain()
    {
        File.WriteAllBytes(DataFile(DataStore.HeroScoresFile), FirstRowEnding(Seed(DataStore.HeroScoresFile), "1"));
        await InstallAsync(ModelUpdatePlan.For(Publish((DataStore.HeroScoresFile, FirstRowEnding(Seed(DataStore.HeroScoresFile), "2"))), _data.Path, false));

        var newer = Publish((DataStore.HeroScoresFile, FirstRowEnding(Seed(DataStore.HeroScoresFile), "3")));

        Assert.Equal([DataStore.HeroScoresFile], ModelUpdatePlan.For(newer, _data.Path, askAgain: false).Edited);
    }

    [Fact]
    public async Task ADamagedDownloadWritesNothing()
    {
        var published = Publish((DataStore.TraitWeightsFile, FirstRowEnding(Seed(DataStore.TraitWeightsFile), "1.3")));
        _api.Bytes[published.UrlOf(DataStore.TraitWeightsFile)] = [1, 2, 3];
        var plan = ModelUpdatePlan.For(published, _data.Path, askAgain: false);

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.DownloadAsync(published, plan.Quiet));
    }

    [Fact]
    public async Task AFileEditedWhileTheUpdateDownloadedIsLeftAlone()
    {
        var published = Publish((DataStore.TraitWeightsFile, FirstRowEnding(Seed(DataStore.TraitWeightsFile), "1.3")));
        var plan = ModelUpdatePlan.For(published, _data.Path, askAgain: false);
        var downloaded = await _service.DownloadAsync(published, plan.Quiet);
        var edit = FirstRowEnding(Seed(DataStore.TraitWeightsFile), "0.9");
        File.WriteAllBytes(DataFile(DataStore.TraitWeightsFile), edit);

        Assert.Empty(ModelUpdateService.Install(_data.Path, plan, downloaded));
        Assert.Equal(edit, File.ReadAllBytes(DataFile(DataStore.TraitWeightsFile)));
        Assert.Equal([DataStore.TraitWeightsFile], ModelUpdatePlan.For(published, _data.Path, askAgain: false).Edited);
    }

    [Fact]
    public void AFolderFromBeforeModelsWerePublishedAsksAboutWhateverDiffers()
    {
        File.Delete(DataFile(ModelManifest.FileName));

        Assert.False(ModelUpdatePlan.For(Publish(), _data.Path, askAgain: false).HasWork);
        var plan = ModelUpdatePlan.For(Publish((DataStore.TraitWeightsFile, FirstRowEnding(Seed(DataStore.TraitWeightsFile), "1.3"))), _data.Path, false);
        Assert.Equal([DataStore.TraitWeightsFile], plan.Edited);
        Assert.Empty(plan.Quiet);
    }

    [Fact]
    public async Task APublishedModelOfAnotherFormatOrNoneAtAllIsLeftAlone()
    {
        Assert.Null(await _service.PublishedAsync());

        var published = Publish();
        _api.Bytes[ModelManifest.ManifestUrl] = (published with { Format = ModelManifest.CurrentFormat + 1 }).ToJsonBytes();

        Assert.Null(await _service.PublishedAsync());
    }

    [Fact]
    public async Task AnUpdateTellsOnlyTheNotesNotShownBefore()
    {
        ModelNote first = new("2026-10-09", "Rated the new heroes."), second = new("2026-10-16", "Spirit items rate higher against Haze.");
        var published = PublishWithNotes([first], (DataStore.TraitWeightsFile, FirstRowEnding(Seed(DataStore.TraitWeightsFile), "1.3")));
        Assert.Equal([first], ModelManifest.Parse((await _service.PublishedAsync())!.ToJsonBytes()).Notes);

        var plan = ModelUpdatePlan.For(published, _data.Path, askAgain: false);
        Assert.Equal([first], plan.News);
        await InstallAsync(plan);

        Assert.Equal([first], ModelManifest.Installed(_data.Path)!.Notes);
        var newer = PublishWithNotes([second, first], (DataStore.TraitWeightsFile, FirstRowEnding(Seed(DataStore.TraitWeightsFile), "1.4")));
        Assert.Equal([second], ModelUpdatePlan.For(newer, _data.Path, askAgain: false).News);
    }

    [Fact]
    public void PublishingWithANoteAddsItNewestFirstButOnlyWithANewVersion()
    {
        using var seed = SeedCopy();
        File.WriteAllBytes(DataFile(DataStore.TraitWeightsFile), FirstRowEnding(Seed(DataStore.TraitWeightsFile), "1.3"));
        var listedNow = () => ModelManifest.Parse(File.ReadAllBytes(Path.Combine(seed.Path, ModelManifest.FileName)));

        Assert.Equal(new ModelNote("2026-10-09", "Weights lean on burst."),
            ModelPublisher.Publish(_data.Path, seed.Path, "2026-10-09", "  Weights lean on burst. ").Note);
        Assert.Null(ModelPublisher.Publish(_data.Path, seed.Path, "2026-10-10", "Nothing changed.").Note);
        File.WriteAllBytes(DataFile(DataStore.TraitWeightsFile), FirstRowEnding(Seed(DataStore.TraitWeightsFile), "1.4"));
        ModelPublisher.Publish(_data.Path, seed.Path, "2026-10-11", "Weights lean on burst even more.");

        var listed = listedNow();
        Assert.Equal(["Weights lean on burst even more.", "Weights lean on burst."], listed.Notes.Select(note => note.Text));
        // The seed check keeps passing: its notes are part of what model.json says.
        Assert.Equal(ModelPublisher.Listing(seed.Path, listed), File.ReadAllBytes(Path.Combine(seed.Path, ModelManifest.FileName)));
    }

    [Fact]
    public async Task NewHeroesAreAddedToTheSeedUnratedAndPublishedWithANote()
    {
        using var seed = SeedCopy();
        var before = DataStore.Load(seed.Path);
        var game = new JsonArray(before.Heroes.Values
            .Select(hero => (JsonNode)new JsonObject { ["id"] = hero.GameId, ["name"] = hero.HeroName })
            .Append(new JsonObject { ["id"] = 9001, ["name"] = "Newcomer" })
            .Append(new JsonObject { ["id"] = 9002, ["name"] = "Latecomer" })
            .ToArray());
        _api.Json[$"{GameSync.Api}/heroes?only_active=true"] = () => game.DeepClone();
        var service = new GameApiService(_api);

        var result = await ModelPublisher.AddNewHeroesAsync(service, seed.Path, "2026-10-09");

        Assert.Equal(["Newcomer", "Latecomer"], result.Added);
        Assert.Equal([DataStore.HeroScoresFile, DataStore.HeroesFile], result.Published.Files);
        Assert.Equal("New heroes: Newcomer and Latecomer. Their ratings are still to come, so recommendations leave them out until then.",
            result.Published.Note?.Text);
        var after = DataStore.Load(seed.Path);
        Assert.Equal(before.Heroes.Keys.Concat(["newcomer", "latecomer"]), after.Heroes.Keys);
        Assert.Equal(before.HeroScores.Count + 2 * after.Categories.Count, after.HeroScores.Count);
        Assert.All(["newcomer", "latecomer"], heroId => Assert.False(after.IsProfiled(heroId)));
        Assert.All(before.HeroScores, pair => Assert.Equal(pair.Value, after.HeroScore(pair.Key.HeroId, pair.Key.CategoryId)));
        // The seed still passes TheSeedsModelJsonListsEveryModelFileByItsHash.
        var listed = ModelManifest.Parse(File.ReadAllBytes(Path.Combine(seed.Path, ModelManifest.FileName)));
        Assert.Equal(ModelPublisher.Listing(seed.Path, listed), File.ReadAllBytes(Path.Combine(seed.Path, ModelManifest.FileName)));
        Assert.Equal("2026-10-09", listed.Published);

        var again = await ModelPublisher.AddNewHeroesAsync(service, seed.Path, "2026-10-10");

        Assert.Empty(again.Added);
        Assert.False(again.Published.Changed);
        Assert.Equal("2026-10-09", ModelManifest.Parse(File.ReadAllBytes(Path.Combine(seed.Path, ModelManifest.FileName))).Published);
    }

    [Fact]
    public async Task OneNewHeroIsNotedInTheSingular()
    {
        using var seed = SeedCopy();
        var game = new JsonArray(new JsonObject { ["id"] = 9001, ["name"] = "Newcomer" });
        _api.Json[$"{GameSync.Api}/heroes?only_active=true"] = () => game.DeepClone();

        var result = await ModelPublisher.AddNewHeroesAsync(new GameApiService(_api), seed.Path, "2026-10-09");

        Assert.Equal("New hero: Newcomer. Its ratings are still to come, so recommendations leave it out until then.", result.Published.Note?.Text);
    }

    [Fact]
    public void PublishingRefusesADataFolderThatLacksAHeroTheSeedHas()
    {
        using var seed = SeedCopy();
        var heroes = Path.Combine(seed.Path, DataStore.HeroesFile);
        File.WriteAllText(heroes, File.ReadAllText(heroes).TrimEnd() + "\r\nnewcomer,Newcomer,9001\r\n");
        var weights = FirstRowEnding(Seed(DataStore.TraitWeightsFile), "1.3");
        File.WriteAllBytes(DataFile(DataStore.TraitWeightsFile), weights);
        var listed = File.ReadAllBytes(Path.Combine(seed.Path, ModelManifest.FileName));

        var refused = Assert.Throws<InvalidOperationException>(() => ModelPublisher.Publish(_data.Path, seed.Path, "2026-10-09"));

        Assert.Contains("Newcomer", refused.Message);
        Assert.Equal(Seed(DataStore.TraitWeightsFile), File.ReadAllBytes(Path.Combine(seed.Path, DataStore.TraitWeightsFile)));
        Assert.Equal(listed, File.ReadAllBytes(Path.Combine(seed.Path, ModelManifest.FileName)));

        // The game id is what names a hero: one the folder spells with another id is the same hero.
        File.WriteAllText(DataFile(DataStore.HeroesFile), File.ReadAllText(DataFile(DataStore.HeroesFile)).TrimEnd() + "\r\nnewcomer_two,Newcomer,9001\r\n");
        Assert.Equal([DataStore.TraitWeightsFile, DataStore.HeroesFile], ModelPublisher.Publish(_data.Path, seed.Path, "2026-10-09").Files);
    }

    // -- new heroes ---------------------------------------------------------------------

    /// <summary>The hero files as a newer model publishes them with one more hero, "Newcomer", rated 3 on the first trait.</summary>
    private (byte[] Heroes, byte[] Scores) WithANewcomer()
    {
        var firstTrait = DataStore.Load(_data.Path).Categories.Keys.First();
        var heroes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Seed(DataStore.HeroesFile)).TrimEnd() + "\r\nnewcomer,Newcomer,9001\r\n");
        var scores = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Seed(DataStore.HeroScoresFile)).TrimEnd() + $"\r\nnewcomer,{firstTrait},3\r\n");
        return (heroes, scores);
    }

    private void AddRowTo(string file, string row) => File.WriteAllText(DataFile(file), File.ReadAllText(DataFile(file)).TrimEnd() + "\r\n" + row + "\r\n");

    private static void InstalledAsPublished(string dataDir) => ModelUpdateService.Record(dataDir, ModelManifest.Of(dataDir, "2026-10-02"));

    [Fact]
    public async Task AChangedHeroesFileGainsTheNewHeroesWithoutAskingAndKeepsItsOwnRows()
    {
        InstalledAsPublished(_data.Path);
        AddRowTo(DataStore.HeroesFile, "mine,Mine,9100");
        var (heroes, scores) = WithANewcomer();
        var published = Publish((DataStore.HeroesFile, heroes), (DataStore.HeroScoresFile, scores));

        Assert.Equal([DataStore.HeroesFile], ModelUpdateService.HeroFilesToMerge(published, _data.Path, askAgain: false));
        var merged = ModelUpdateService.AddNewHeroes(_data.Path, [DataStore.HeroesFile], await _service.DownloadAsync(published, ModelUpdateService.HeroFiles));

        Assert.Equal(["Newcomer"], merged.Added);
        Assert.Equal([DataStore.HeroesFile], merged.Written);
        var store = DataStore.Load(_data.Path);
        Assert.Equal(["mine", "newcomer"], store.Heroes.Keys.TakeLast(2));
        Assert.Equal(9001, store.Heroes["newcomer"].GameId);
        // Its owner is asked about nothing, and what nobody changed still updates quietly, bringing the ratings.
        var plan = ModelUpdatePlan.For(published, _data.Path, askAgain: false);
        Assert.Empty(plan.Edited);
        Assert.Equal([DataStore.HeroScoresFile], plan.Quiet);
        Assert.Equal([DataStore.HeroesFile], plan.Additive);
        await InstallAsync(plan);
        Assert.Equal(scores, File.ReadAllBytes(DataFile(DataStore.HeroScoresFile)));
        var again = ModelUpdatePlan.For(published, _data.Path, askAgain: false);
        Assert.False(again.HasWork);
        Assert.Empty(again.Additive);
        Assert.Empty(ModelUpdateService.HeroFilesToMerge(published, _data.Path, askAgain: true));
    }

    [Fact]
    public async Task ChangedRatingsGainTheNewHeroesRowsAndKeepTheOwnersEdits()
    {
        InstalledAsPublished(_data.Path);
        var mine = ModelUpdateTests.FirstRowEnding(Seed(DataStore.HeroScoresFile), "1");
        File.WriteAllBytes(DataFile(DataStore.HeroScoresFile), mine);
        var (heroes, scores) = WithANewcomer();
        var published = Publish((DataStore.HeroesFile, heroes), (DataStore.HeroScoresFile, scores));

        // The heroes file is unchanged here, so the update replaces it whole; only the ratings need the merge.
        Assert.Equal([DataStore.HeroScoresFile], ModelUpdateService.HeroFilesToMerge(published, _data.Path, askAgain: false));
        var merged = ModelUpdateService.AddNewHeroes(_data.Path, [DataStore.HeroScoresFile], await _service.DownloadAsync(published, ModelUpdateService.HeroFiles));

        Assert.Empty(merged.Added);
        Assert.Equal([DataStore.HeroScoresFile], merged.Written);
        Assert.Equal(Seed(DataStore.HeroesFile), File.ReadAllBytes(DataFile(DataStore.HeroesFile)));
        var text = File.ReadAllText(DataFile(DataStore.HeroScoresFile));
        Assert.StartsWith(Encoding.UTF8.GetString(mine).TrimEnd(), text);
        Assert.EndsWith(",3", text.TrimEnd());
        Assert.Contains("\r\nnewcomer,", text);
        // What's left to ask about is the owner's own ratings, as before.
        var plan = ModelUpdatePlan.For(published, _data.Path, askAgain: false);
        Assert.Equal([DataStore.HeroScoresFile], plan.Edited);
        Assert.Equal([DataStore.HeroesFile], plan.Quiet);
    }

    [Fact]
    public async Task AHeroTheFolderSpellsWithAnotherIdIsTheSameHeroAndIsntAddedTwice()
    {
        AddRowTo(DataStore.HeroesFile, "newcomer_two,Newcomer,9001");
        var before = File.ReadAllBytes(DataFile(DataStore.HeroesFile));
        var (heroes, scores) = WithANewcomer();
        var published = Publish((DataStore.HeroesFile, heroes), (DataStore.HeroScoresFile, scores));

        var merged = ModelUpdateService.AddNewHeroes(_data.Path, ModelUpdateService.HeroFiles, await _service.DownloadAsync(published, ModelUpdateService.HeroFiles));

        Assert.Empty(merged.Added);
        Assert.Empty(merged.Written);
        Assert.Equal(before, File.ReadAllBytes(DataFile(DataStore.HeroesFile)));
    }

    [Fact]
    public async Task OnlyTheTraitsTheFolderHasAreTakenForANewHero()
    {
        InstalledAsPublished(_data.Path);
        File.WriteAllBytes(DataFile(DataStore.HeroScoresFile), FirstRowEnding(Seed(DataStore.HeroScoresFile), "1"));
        var (heroes, scores) = WithANewcomer();
        scores = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(scores).TrimEnd() + "\r\nnewcomer,a_trait_this_folder_lacks,5\r\n");
        var published = Publish((DataStore.HeroesFile, heroes), (DataStore.HeroScoresFile, scores));

        ModelUpdateService.AddNewHeroes(_data.Path, [DataStore.HeroScoresFile], await _service.DownloadAsync(published, ModelUpdateService.HeroFiles));

        Assert.DoesNotContain("a_trait_this_folder_lacks", File.ReadAllText(DataFile(DataStore.HeroScoresFile)));
        Assert.Equal(1, File.ReadAllLines(DataFile(DataStore.HeroScoresFile)).Count(line => line.StartsWith("newcomer,", StringComparison.Ordinal)));
    }

    [Fact]
    public void AFileKeptOverThisVersionIsntMergedIntoAgainUnlessAskedFor()
    {
        InstalledAsPublished(_data.Path);
        File.WriteAllBytes(DataFile(DataStore.HeroScoresFile), FirstRowEnding(Seed(DataStore.HeroScoresFile), "1"));
        var (heroes, scores) = WithANewcomer();
        var published = Publish((DataStore.HeroesFile, heroes), (DataStore.HeroScoresFile, scores));
        var installed = ModelManifest.Installed(_data.Path)!;
        ModelUpdateService.Record(_data.Path, installed with { Kept = new Dictionary<string, string> { [DataStore.HeroScoresFile] = published.Files[DataStore.HeroScoresFile] } });

        Assert.Empty(ModelUpdateService.HeroFilesToMerge(published, _data.Path, askAgain: false));
        Assert.Equal([DataStore.HeroScoresFile], ModelUpdateService.HeroFilesToMerge(published, _data.Path, askAgain: true));
    }

    [Fact]
    public void TheReleaseHoldsEachFileUnderTheNameItsHashGivesIt()
    {
        using var seed = SeedCopy();
        using var release = new TempDirectory();
        var listed = ModelManifest.Parse(Seed(ModelManifest.FileName));

        var names = ModelPublisher.WriteAssets(seed.Path, release.Path);

        Assert.Equal(listed.Files.Select(pair => ModelManifest.AssetOf(pair.Key, pair.Value)).Append(ModelManifest.FileName), names);
        Assert.Equal(names.Order(), Directory.GetFiles(release.Path).Select(Path.GetFileName).Order());
        Assert.Equal($"trait_weights-{listed.Files[DataStore.TraitWeightsFile][..8]}.csv", ModelManifest.AssetOf(DataStore.TraitWeightsFile, listed.Files[DataStore.TraitWeightsFile]));
        Assert.EndsWith("/" + names[0], listed.UrlOf(listed.Files.Keys.First()));
        Assert.Equal(File.ReadAllBytes(Path.Combine(seed.Path, ModelManifest.FileName)), File.ReadAllBytes(Path.Combine(release.Path, ModelManifest.FileName)));

        // A file that isn't the one model.json lists stops it.
        File.WriteAllBytes(Path.Combine(seed.Path, DataStore.TraitWeightsFile), FirstRowEnding(Seed(DataStore.TraitWeightsFile), "1.3"));
        Assert.Throws<InvalidOperationException>(() => ModelPublisher.WriteAssets(seed.Path, release.Path));
    }

    [Fact]
    public async Task AResetOffersEveryFileThatDiffersEvenWithNothingNewerPublished()
    {
        var mine = FirstRowEnding(Seed(DataStore.HeroScoresFile), "1");
        File.WriteAllBytes(DataFile(DataStore.HeroScoresFile), mine);
        var published = Publish();
        Assert.False(ModelUpdatePlan.For(published, _data.Path, askAgain: true).HasWork);

        var reset = ModelUpdatePlan.Reset(published, _data.Path);
        Assert.Equal([DataStore.HeroScoresFile], reset.Edited);
        Assert.Empty(reset.Quiet);
        await InstallAsync(reset, DataStore.HeroScoresFile);

        Assert.Equal(Seed(DataStore.HeroScoresFile), File.ReadAllBytes(DataFile(DataStore.HeroScoresFile)));
        Assert.False(ModelUpdatePlan.Reset(published, _data.Path).HasWork);
        // And it can be undone, like an update.
        Assert.Equal([DataStore.HeroScoresFile], ModelUpdateService.Undo(_data.Path));
        Assert.Equal(mine, File.ReadAllBytes(DataFile(DataStore.HeroScoresFile)));
    }

    [Fact]
    public async Task UndoingAnUpdatePutsBackWhatItReplacedWithoutItBeingOfferedAgain()
    {
        var weights = FirstRowEnding(Seed(DataStore.TraitWeightsFile), "1.3");
        var published = Publish((DataStore.TraitWeightsFile, weights));
        Assert.Empty(ModelUpdateService.Undoable(_data.Path));
        await InstallAsync(ModelUpdatePlan.For(published, _data.Path, askAgain: false));
        Assert.Equal([DataStore.TraitWeightsFile], ModelUpdateService.Undoable(_data.Path));

        Assert.Equal([DataStore.TraitWeightsFile], ModelUpdateService.Undo(_data.Path));

        Assert.Equal(Seed(DataStore.TraitWeightsFile), File.ReadAllBytes(DataFile(DataStore.TraitWeightsFile)));
        Assert.Empty(ModelUpdateService.Undoable(_data.Path));
        Assert.False(ModelUpdatePlan.For(published, _data.Path, askAgain: true).HasWork);
        Assert.Equal([DataStore.TraitWeightsFile], ModelUpdatePlan.Reset(published, _data.Path).Edited);
        // A newer version asks rather than replacing it quietly.
        var newer = Publish((DataStore.TraitWeightsFile, FirstRowEnding(Seed(DataStore.TraitWeightsFile), "1.4")));
        Assert.Equal([DataStore.TraitWeightsFile], ModelUpdatePlan.For(newer, _data.Path, askAgain: false).Edited);
    }

    [Fact]
    public async Task AnUpdateIsntUndoneOverAnEditMadeSince()
    {
        var published = Publish((DataStore.TraitWeightsFile, FirstRowEnding(Seed(DataStore.TraitWeightsFile), "1.3")));
        await InstallAsync(ModelUpdatePlan.For(published, _data.Path, askAgain: false));
        var edit = FirstRowEnding(Seed(DataStore.TraitWeightsFile), "0.9");
        File.WriteAllBytes(DataFile(DataStore.TraitWeightsFile), edit);

        Assert.Empty(ModelUpdateService.Undoable(_data.Path));
        Assert.Empty(ModelUpdateService.Undo(_data.Path));
        Assert.Equal(edit, File.ReadAllBytes(DataFile(DataStore.TraitWeightsFile)));
    }
}