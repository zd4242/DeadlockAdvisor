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

    private const string _brokenItems = "item_id,item_name,category,tier\r\nbroken,Broken,weapon,not-a-number\r\n";

    [Fact]
    public void ABadRowRestoresFromTheNewestBackupThatLoads()
    {
        var items = Path.Combine(_service.DataDir, DataStore.ItemsFile);
        var good = File.ReadAllBytes(items);
        WriteBackup(DataStore.ItemsFile, "20261001-120000", [.. good, .. "\r\n"u8]);
        WriteBackup(DataStore.ItemsFile, "20261002-120000", good);
        WriteBackup(DataStore.ItemsFile, "20261003-120000", System.Text.Encoding.UTF8.GetBytes(_brokenItems));
        File.WriteAllText(items, _brokenItems);

        var (service, messages) = Start();
        using var owner = service;

        Assert.Equal(good, File.ReadAllBytes(items));
        Assert.Equal(_fixture.Data.Store.Items.Count, service.Store.Items.Count);
        Assert.Equal(_brokenItems, File.ReadAllText(Assert.Single(BadCopies(DataStore.ItemsFile))));
        var message = Assert.Single(messages);
        Assert.Equal(NotificationSeverity.Warning, message.Severity);
        Assert.Contains("items.csv couldn't be read", message.Message);
        Assert.Contains("invalid literal for int()", message.Message);
        Assert.Contains("the backup from 2026-10-02 12:00", message.Message);
        Assert.Contains(".bad-", message.Message);
    }

    [Fact]
    public void WithoutAGoodBackupTheBundledCopyTakesOver()
    {
        var items = Path.Combine(_service.DataDir, DataStore.ItemsFile);
        WriteBackup(DataStore.ItemsFile, "20261003-120000", System.Text.Encoding.UTF8.GetBytes(_brokenItems));
        File.WriteAllText(items, _brokenItems);

        var (service, messages) = Start();
        using var owner = service;

        AssertEx.BytesEqual(Path.Combine(SeedDir(), DataStore.ItemsFile), items);
        Assert.NotEmpty(service.Store.Items);
        Assert.Contains("the bundled copy", Assert.Single(messages).Message);
    }

    [Fact]
    public void AnEmptyBaseTableCountsAsDamaged()
    {
        var items = Path.Combine(_service.DataDir, DataStore.ItemsFile);
        File.WriteAllBytes(items, []);

        var (service, messages) = Start();
        using var owner = service;

        Assert.NotEmpty(service.Store.Items);
        Assert.Contains("it has no rows", Assert.Single(messages).Message);
        Assert.Empty(File.ReadAllBytes(Assert.Single(BadCopies(DataStore.ItemsFile))));
    }

    [Fact]
    public void SeveralDamagedFilesAreEachRestoredAndReportedTogether()
    {
        File.WriteAllText(Path.Combine(_service.DataDir, DataStore.ItemsFile), _brokenItems);
        File.WriteAllText(Path.Combine(_service.DataDir, DataStore.CategoriesFile), "category_id,category_name,scale_min,scale_max,description\r\nx,X,low,high,\r\n");
        File.WriteAllText(Path.Combine(_service.DataDir, DataStore.ItemTooltipsFile), "{ not json");

        var (service, messages) = Start();
        using var owner = service;

        Assert.NotEmpty(service.Store.Items);
        Assert.NotEmpty(service.Store.Categories);
        Assert.NotEmpty(service.Store.ItemTooltips);
        var message = Assert.Single(messages).Message;
        Assert.Contains("items.csv couldn't be read", message);
        Assert.Contains("categories.csv couldn't be read", message);
        Assert.Contains("item_tooltips.json couldn't be read", message);
        Assert.Single(BadCopies(DataStore.ItemsFile));
        Assert.Single(BadCopies(DataStore.CategoriesFile));
        Assert.Single(BadCopies(DataStore.ItemTooltipsFile));
    }

    [Fact]
    public void AHealthyFolderSaysNothingAndLeavesNoCopies()
    {
        var (service, messages) = Start();
        using var owner = service;

        Assert.Empty(messages);
        Assert.Empty(Directory.GetFiles(_service.DataDir, "*.bad-*"));
    }

    [Fact]
    public void ReloadRepairsTheCurrentFolderToo()
    {
        var (service, messages) = Start();
        using var owner = service;
        var replaced = 0;
        using var __ = service.StoreReplaced.Subscribe(_ => replaced++);
        File.WriteAllText(Path.Combine(service.DataDir, DataStore.ItemsFile), _brokenItems);

        service.Reload();

        Assert.NotEmpty(service.Store.Items);
        Assert.Equal(1, replaced);
        Assert.Contains("items.csv couldn't be read", Assert.Single(messages).Message);
        Assert.Single(BadCopies(DataStore.ItemsFile));
    }

    [Fact]
    public void AFolderChosenInChangeDataFolderIsRefusedNotRepaired()
    {
        using var other = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(other.Path, "data"));
        foreach (var file in Directory.GetFiles(Golden.DataDir))
            File.Copy(file, Path.Combine(other.Path, "data", Path.GetFileName(file)));
        var items = Path.Combine(other.Path, "data", DataStore.ItemsFile);
        File.WriteAllText(items, _brokenItems);

        var failure = Assert.Throws<DataLoadException>(() => _service.ChangeDataRoot(other.Path));

        Assert.Equal(DataStore.ItemsFile, failure.File);
        Assert.Equal(_brokenItems, File.ReadAllText(items));
        Assert.Empty(Directory.GetFiles(Path.Combine(other.Path, "data"), "*.bad-*"));
        Assert.Equal(_root.Path, _service.DataRoot);
    }

    /// <summary>A second service over the same folder, started after the test damaged it, with the messages it sent.</summary>
    private (DataService Service, List<Notification> Messages) Start()
    {
        var notifications = new NotificationService(new FakeLoggingService());
        var service = new DataService(_settings, new FakeLoggingService(), notifications, _clock);
        service.Initialize();
        var messages = new List<Notification>();
        notifications.Notifications.Subscribe(messages.Add);
        return (service, messages);
    }

    private void WriteBackup(string file, string stamp, byte[] contents)
    {
        var dir = Path.Combine(_service.DataDir, BackedUpFile.BackupFolderName);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(file)}.{stamp}{Path.GetExtension(file)}"), contents);
    }

    private string[] BadCopies(string file) => Directory.GetFiles(_service.DataDir, file + ".bad-*");

    private static string SeedDir() => Path.Combine(UiRepoRoot(), "src", "Assets", "SeedData");

    private static string UiRepoRoot() => Ui.UiHarness.RepoRoot();
}
