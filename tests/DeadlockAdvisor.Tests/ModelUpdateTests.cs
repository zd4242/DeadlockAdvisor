using System.Text;
using DeadlockAdvisor.Services;
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
    private ModelManifest Publish(params (string File, byte[] Bytes)[] changed)
    {
        var files = new Dictionary<string, string>();
        foreach (var file in ModelManifest.ModelFiles)
        {
            var bytes = changed.Any(change => change.File == file) ? changed.First(change => change.File == file).Bytes : Seed(file);
            _api.Bytes[ModelManifest.UrlOf(file)] = bytes;
            files[file] = ModelManifest.Hash(bytes);
        }
        var published = new ModelManifest(ModelManifest.CurrentFormat, "2026-10-09", files, new Dictionary<string, string>());
        _api.Bytes[ModelManifest.UrlOf(ModelManifest.FileName)] = published.ToJsonBytes();
        return published;
    }

    private async Task<IReadOnlySet<string>> InstallAsync(ModelUpdatePlan plan, params string[] replace) =>
        ModelUpdateService.Install(_data.Path, plan, await _service.DownloadAsync(plan.Published, plan.Quiet.Concat(plan.Edited.Where(replace.Contains))));

    [Fact]
    public void TheSeedsModelJsonListsEveryModelFileByItsHash()
    {
        var path = Path.Combine(SeedDir, ModelManifest.FileName);
        var listed = File.Exists(path) ? ModelManifest.Parse(File.ReadAllBytes(path)) : null;
        var actual = ModelManifest.Of(SeedDir, listed?.Published ?? "");
        if (Golden.Updating && (listed is null || !actual.ToJsonBytes().SequenceEqual(File.ReadAllBytes(path))))
        {
            // Changed files are a new version: published today.
            File.WriteAllBytes(path, (actual with { Published = DateTime.UtcNow.ToString("yyyy-MM-dd") }).ToJsonBytes());
            return;
        }

        Assert.True(listed is not null, $"Regenerate {path} with DEADLOCK_UPDATE_GOLDENS=1.");
        Assert.Equal(ModelManifest.ModelFiles, listed.Files.Keys);
        Assert.True(actual.ToJsonBytes().SequenceEqual(File.ReadAllBytes(path)),
            "The seed's files changed: regenerate model.json with DEADLOCK_UPDATE_GOLDENS=1, which publishes them as a new version once pushed.");
    }

    /// <summary>A copy of the seed to publish into.</summary>
    private static TempDirectory SeedCopy()
    {
        var seed = new TempDirectory();
        foreach (var file in Directory.GetFiles(SeedDir))
            File.Copy(file, Path.Combine(seed.Path, Path.GetFileName(file)));
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
        _api.Bytes[ModelManifest.UrlOf(DataStore.TraitWeightsFile)] = [1, 2, 3];
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
        _api.Bytes[ModelManifest.UrlOf(ModelManifest.FileName)] = (published with { Format = ModelManifest.CurrentFormat + 1 }).ToJsonBytes();

        Assert.Null(await _service.PublishedAsync());
    }
}
