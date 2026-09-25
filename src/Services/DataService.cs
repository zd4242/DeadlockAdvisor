using System.IO;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;

namespace DeadlockAdvisor.Services;

public class DataService : IDataService, IDisposable
{
    public static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);
    public static readonly TimeSpan RescoreThrottle = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan _savedLabelFor = TimeSpan.FromMilliseconds(1500);

    private const string _seedPrefix = "SeedData/";

    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;
    private readonly INotificationService _notificationService;
    private readonly IScheduler _scheduler;

    private readonly Subject<Unit> _storeReplaced = new();
    private readonly Subject<Unit> _scoresChanged = new();
    private readonly BehaviorSubject<SaveState> _saveStates = new(SaveState.Idle);
    private readonly SerialDisposable _saveTimer = new();
    private readonly SerialDisposable _rescoreTimer = new();
    private readonly SerialDisposable _savedTimer = new();

    private DataFiles _pending;
    private bool _rescorePending;

    public string DataRoot { get; private set; } = "";
    public string DataDir => Path.Combine(DataRoot, "data");
    public string AssetsDir => Path.Combine(DataRoot, "assets");

    public DataStore Store { get; private set; } = new("");
    public IReadOnlyDictionary<MatrixKey, double> Matrix { get; private set; } = new Dictionary<MatrixKey, double>();

    public IObservable<Unit> StoreReplaced => _storeReplaced;
    public IObservable<Unit> ScoresChanged => _scoresChanged;
    public IObservable<SaveState> SaveStates => _saveStates;

    /// <summary>The data folder used unless the settings name another one.</summary>
    public static string DefaultDataRoot => JsonSettingsService.AppDataPath;

    public DataService(ISettingsService settingsService, ILoggingService loggingService, INotificationService notificationService)
        : this(settingsService, loggingService, notificationService, RxApp.MainThreadScheduler)
    {
    }

    internal DataService(ISettingsService settingsService, ILoggingService loggingService,
        INotificationService notificationService, IScheduler scheduler)
    {
        _settingsService = settingsService;
        _loggingService = loggingService;
        _notificationService = notificationService;
        _scheduler = scheduler;
    }

    public void Initialize()
    {
        var configured = _settingsService.Current.DataRoot;
        var root = string.IsNullOrWhiteSpace(configured) ? DefaultDataRoot : configured;
        try
        {
            Open(root);
        }
        catch (Exception ex) when (!IsDefault(root))
        {
            // A data folder that went missing (an unplugged drive, a moved repo) mustn't stop the
            // app starting: fall back to the default one, and say so.
            _notificationService.ShowError($"Couldn't load the data folder {root}; using the default one instead. {ex.Message}", ex);
            Open(DefaultDataRoot);
        }
    }

    private void Open(string root)
    {
        if (IsDefault(root))
            SeedIfEmpty(Path.Combine(root, "data"));
        EnsureArtFolders(Path.Combine(root, "assets"));

        Store = DataStore.Load(Path.Combine(root, "data"));
        DataRoot = root;
        RebuildMatrix();
    }

    private static bool IsDefault(string root) =>
        string.Equals(Path.GetFullPath(root).TrimEnd('\\', '/'), Path.GetFullPath(DefaultDataRoot).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Write the bundled copy of the data into a data folder that doesn't have any yet.</summary>
    internal static void SeedIfEmpty(string dataDir)
    {
        if (File.Exists(Path.Combine(dataDir, DataStore.HeroesFile)))
            return;

        Directory.CreateDirectory(dataDir);
        var assembly = typeof(DataService).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(_seedPrefix, StringComparison.Ordinal)))
        {
            var target = Path.Combine(dataDir, name[_seedPrefix.Length..]);
            if (File.Exists(target))
                continue;
            using var source = assembly.GetManifestResourceStream(name)!;
            using var file = File.Create(target);
            source.CopyTo(file);
        }
    }

    private static void EnsureArtFolders(string assetsDir)
    {
        foreach (var (folder, readme) in ArtReadmes.All)
        {
            var dir = Path.Combine(assetsDir, folder);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "README.txt");
            if (!File.Exists(path))
                File.WriteAllText(path, readme);
        }
    }

    public void MarkEdited(DataFiles files)
    {
        _pending |= files;
        _saveStates.OnNext(SaveState.Saving);
        _savedTimer.Disposable = null;
        _saveTimer.Disposable = _scheduler.Schedule(SaveDebounce, () => FlushSaves());

        // Rescore soon but not on every keystroke: fast typing mustn't queue up rebuilds.
        if (_rescorePending)
            return;
        _rescorePending = true;
        _rescoreTimer.Disposable = _scheduler.Schedule(RescoreThrottle, () =>
        {
            _rescorePending = false;
            RebuildMatrix();
            _scoresChanged.OnNext(Unit.Default);
        });
    }

    public bool FlushSaves()
    {
        _saveTimer.Disposable = null;
        if (_pending == DataFiles.None)
            return true;

        try
        {
            if (_pending.HasFlag(DataFiles.HeroScores))
                Store.SaveHeroScores();
            if (_pending.HasFlag(DataFiles.ItemCoefficients))
                Store.SaveItemCoefficients();
            if (_pending.HasFlag(DataFiles.TraitWeights))
                Store.SaveTraitWeights();
            if (_pending.HasFlag(DataFiles.StatRules))
                Store.SaveStatRules();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _saveStates.OnNext(SaveState.Failed);
            _notificationService.ShowError(
                $"Writing to {DataDir} failed: {ex.Message} Your edits are still in memory; fix the problem and edit anything to retry.", ex);
            return false;
        }

        _pending = DataFiles.None;
        _saveStates.OnNext(SaveState.Saved);
        _savedTimer.Disposable = _scheduler.Schedule(_savedLabelFor, () => _saveStates.OnNext(SaveState.Idle));
        return true;
    }

    public void Reload()
    {
        FlushSaves();
        Store = DataStore.Load(DataDir);
        NotifyReplaced();
    }

    public void NotifyReplaced()
    {
        RebuildMatrix();
        _storeReplaced.OnNext(Unit.Default);
    }

    public void ChangeDataRoot(string folder)
    {
        var root = Path.GetFullPath(folder);
        if (!File.Exists(Path.Combine(root, "data", DataStore.HeroesFile))
            && File.Exists(Path.Combine(root, DataStore.HeroesFile)))
        {
            root = Path.GetDirectoryName(root)!;
        }
        if (!IsDefault(root) && !File.Exists(Path.Combine(root, "data", DataStore.HeroesFile)))
            throw new FileNotFoundException($"{root} has no data{Path.DirectorySeparatorChar}{DataStore.HeroesFile}.");

        FlushSaves();
        if (IsDefault(root))
            SeedIfEmpty(Path.Combine(root, "data"));
        // Loaded before anything is switched, so a folder that fails to load changes nothing.
        var store = DataStore.Load(Path.Combine(root, "data"));
        EnsureArtFolders(Path.Combine(root, "assets"));
        Store = store;
        DataRoot = root;
        _settingsService.Update(s => s.DataRoot = IsDefault(root) ? null : root);
        NotifyReplaced();
    }

    private void RebuildMatrix() => Matrix = ItemScoring.BuildWeightMatrix(Store);

    public void Dispose()
    {
        _saveTimer.Dispose();
        _rescoreTimer.Dispose();
        _savedTimer.Dispose();
    }
}
