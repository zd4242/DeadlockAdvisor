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

    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly BehaviorSubject<AppSettings> _settingsSubject;
    private readonly ILoggingService _loggingService;
    private readonly System.Threading.SemaphoreSlim _fileWriteLock = new(1, 1);

    public AppSettings Current => _settingsSubject.Value;
    public IObservable<AppSettings> SettingsChanged => _settingsSubject;

    public static string AppDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppDataFolderName);

    public JsonSettingsService(ILoggingService loggingService) : this(loggingService, AppDataPath)
    {
    }

    internal JsonSettingsService(ILoggingService loggingService, string folder)
    {
        _loggingService = loggingService;

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
        }
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
