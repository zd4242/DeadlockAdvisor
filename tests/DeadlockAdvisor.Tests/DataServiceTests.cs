using System.Reactive.Concurrency;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

public sealed class DataServiceTests : IDisposable
{
    private readonly DataFixture _fixture = new();
    private readonly TempDirectory _root;
    private readonly FakeSettingsService _settings;
    private readonly HistoricalScheduler _clock;
    private readonly DataService _service;

    public DataServiceTests()
    {
        (_root, _settings, _clock, _service) = (_fixture.Root, _fixture.Settings, _fixture.Clock, _fixture.Data);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void SeedingWritesTheBundledDataVerbatim()
    {
        using var empty = new TempDirectory();

        DataService.SeedIfEmpty(empty.Path);

        var seed = Path.Combine(UiRepoRoot(), "src", "Assets", "SeedData");
        var seeded = Directory.GetFiles(empty.Path).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(Directory.GetFiles(seed).Select(Path.GetFileName).Order(), seeded);
        foreach (var name in seeded)
            AssertEx.BytesEqual(Path.Combine(seed, name!), Path.Combine(empty.Path, name!));
    }

    [Fact]
    public void SeedingNeverOverwritesExistingData()
    {
        using var folder = new TempDirectory();
        File.WriteAllText(folder.File("heroes.csv"), "hero_id,hero_name\n");

        DataService.SeedIfEmpty(folder.Path);

        Assert.Equal(["heroes.csv"], Directory.GetFiles(folder.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void EditsSaveAfterTheDebounceAndRescoreSooner()
    {
        var states = new List<SaveState>();
        var rescored = 0;
        using var _ = _service.SaveStates.Subscribe(states.Add);
        using var __ = _service.ScoresChanged.Subscribe(_ => rescored++);
        var heroScores = Path.Combine(_service.DataDir, DataStore.HeroScoresFile);
        var before = File.ReadAllBytes(heroScores);

        _service.Store.SetHeroScore("abrams", "max_hp", 77);
        _service.MarkEdited(DataFiles.HeroScores);
        _clock.AdvanceBy(DataService.RescoreThrottle);
        Assert.Equal(1, rescored);
        Assert.Equal(before, File.ReadAllBytes(heroScores));

        // A second edit inside the window pushes the save back rather than writing twice.
        _clock.AdvanceBy(TimeSpan.FromMilliseconds(400));
        _service.Store.SetHeroScore("abrams", "max_hp", 78);
        _service.MarkEdited(DataFiles.HeroScores);
        _clock.AdvanceBy(TimeSpan.FromMilliseconds(400));
        Assert.Equal(before, File.ReadAllBytes(heroScores));

        _clock.AdvanceBy(DataService.SaveDebounce);
        Assert.Equal(78, DataStore.Load(_service.DataDir).HeroScore("abrams", "max_hp"));
        Assert.Equal([SaveState.Idle, SaveState.Saving, SaveState.Saving, SaveState.Saved], states);

        _clock.AdvanceBy(TimeSpan.FromSeconds(2));
        Assert.Equal(SaveState.Idle, states[^1]);
    }

    [Fact]
    public void FlushWritesOnlyTheFilesThatChanged()
    {
        var weights = Path.Combine(_service.DataDir, DataStore.TraitWeightsFile);
        var coefficients = Path.Combine(_service.DataDir, DataStore.ItemCoefficientsFile);
        var coefficientsTime = File.GetLastWriteTimeUtc(coefficients);

        _service.Store.SetTraitWeight("max_hp", Relation.As, 1.5);
        _service.MarkEdited(DataFiles.TraitWeights);
        Assert.True(_service.FlushSaves());

        Assert.Contains("max_hp,as,1.5", File.ReadAllText(weights));
        Assert.Equal(coefficientsTime, File.GetLastWriteTimeUtc(coefficients));
    }

    [Fact]
    public void ReloadPicksUpFilesEditedOutsideTheApp()
    {
        var replaced = 0;
        using var _ = _service.StoreReplaced.Subscribe(_ => replaced++);
        var path = Path.Combine(_service.DataDir, DataStore.TraitWeightsFile);
        File.WriteAllText(path, "category_id,relation,weight\r\nmax_hp,with,3\r\n");

        _service.Reload();

        Assert.Equal(3, _service.Store.TraitWeight("max_hp", Relation.With));
        Assert.Equal(1, replaced);
    }

    [Fact]
    public void ChangingTheDataFolderAcceptsTheDataSubfolderAndRemembersTheRoot()
    {
        using var other = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(other.Path, "data"));
        foreach (var file in Directory.GetFiles(Golden.DataDir))
            File.Copy(file, Path.Combine(other.Path, "data", Path.GetFileName(file)));

        _service.ChangeDataRoot(Path.Combine(other.Path, "data"));

        Assert.Equal(other.Path, _service.DataRoot);
        Assert.Equal(other.Path, _settings.Current.DataRoot);
        Assert.True(Directory.Exists(Path.Combine(other.Path, "assets", "heroes")));
    }

    [Fact]
    public void AFolderWithoutDataIsRefusedAndNothingChanges()
    {
        using var empty = new TempDirectory();

        Assert.Throws<FileNotFoundException>(() => _service.ChangeDataRoot(empty.Path));
        Assert.Equal(_root.Path, _service.DataRoot);
        Assert.Equal(_root.Path, _settings.Current.DataRoot);
    }

    private static string UiRepoRoot() => Ui.UiHarness.RepoRoot();
}
