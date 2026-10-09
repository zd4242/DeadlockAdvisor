using System.IO;
using System.Reactive.Subjects;
using System.Text.Json;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

public class JsonSettingsService : ISettingsService
{
    public const string AppDataFolderName = "DeadlockAdvisor";
    private const string _saveFileName = "settings.json";
    private const string _badFilePrefix = "settings.bad-";
    private const int _badFilesKept = 3;

    private readonly string _filePath;
    private readonly INotificationService? _notificationService;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly BehaviorSubject<AppSettings> _settingsSubject;
    private readonly ILoggingService _loggingService;
    private readonly System.Threading.SemaphoreSlim _fileWriteLock = new(1, 1);

    public AppSettings Current => _settingsSubject.Value;
    public IObservable<AppSettings> SettingsChanged => _settingsSubject;

    /// <summary>Names another folder for the settings, the log and the default data, to try the app without touching your own.</summary>
    public const string HomeVariable = "DEADLOCK_ADVISOR_HOME";

    /// <summary>%AppData%\DeadlockAdvisor, unless <see cref="HomeVariable"/> names another folder.</summary>
    public static string AppDataPath =>
        Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home
            ? Path.GetFullPath(home)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppDataFolderName);

    public JsonSettingsService(ILoggingService loggingService, INotificationService notificationService)
        : this(loggingService, AppDataPath, notificationService)
    {
    }

    internal JsonSettingsService(ILoggingService loggingService, string folder, INotificationService? notificationService = null)
    {
        _loggingService = loggingService;
        _notificationService = notificationService;

        Directory.CreateDirectory(folder);

        _filePath = Path.Combine(folder, _saveFileName);
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        _settingsSubject = new BehaviorSubject<AppSettings>(new AppSettings());
    }

    public async Task LoadAsync()
    {
        if (!File.Exists(_filePath))
            return;

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _jsonOptions).ConfigureAwait(false);
            if (settings is not null)
                _settingsSubject.OnNext(settings);
        }
        catch (Exception ex)
        {
            _loggingService.Error($"Failed to load settings from {_filePath}; using defaults", ex);
            KeepUnreadableFile();
        }
    }

    /// <summary>The next <see cref="Update"/> writes the defaults over the file, so keep a copy of what couldn't be read.</summary>
    private void KeepUnreadableFile()
    {
        string? copy = null;
        try
        {
            var folder = Path.GetDirectoryName(_filePath)!;
            copy = Path.Combine(folder, $"{_badFilePrefix}{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(_filePath, copy, overwrite: true);

            var older = Directory.GetFiles(folder, $"{_badFilePrefix}*.json")
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Skip(_badFilesKept);
            foreach (var path in older)
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _loggingService.Warning($"Couldn't keep a copy of the unreadable {_saveFileName}: {ex.Message}");
            copy = null;
        }

        _notificationService?.ShowWarning(copy is null
            ? $"{_saveFileName} couldn't be read, so the default settings are in use."
            : $"{_saveFileName} couldn't be read, so the default settings are in use. The old file is kept as {Path.GetFileName(copy)}.",
            TimeSpan.FromSeconds(10));
    }

    public void Update(Action<AppSettings> mutate)
    {
        var settings = _settingsSubject.Value;
        mutate(settings);
        _settingsSubject.OnNext(settings);
        _ = SaveAsync(settings);
    }

    private async Task SaveAsync(AppSettings settings)
    {
        // Serialized on the caller's thread so the snapshot can't observe a later Update mid-write.
        // Updates can arrive in quick succession (e.g. dragging a splitter), so writes queue in order.
        var json = JsonSerializer.SerializeToUtf8Bytes(settings, _jsonOptions);

        await _fileWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await AtomicFile.WriteAsync(_filePath, stream => stream.WriteAsync(json).AsTask()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _loggingService.Error($"Failed to save settings to {_filePath}", ex);
        }
        finally
        {
            _fileWriteLock.Release();
        }
    }
}
