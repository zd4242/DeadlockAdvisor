using System.IO;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

/// <summary>
/// Logs to the console and to a file beside the settings, so a failure in the published exe (which
/// has no console) can still be diagnosed afterwards. The previous run's log is kept alongside.
/// </summary>
public class LoggingService : ILoggingService
{
    public const string FileName = "deadlock-advisor.log";
    public const string PreviousFileName = "deadlock-advisor.previous.log";

    private readonly object _lock = new();
    private readonly string? _logFile;

    public LoggingService() : this(JsonSettingsService.AppDataPath)
    {
    }

    internal LoggingService(string? folder)
    {
        if (folder is null)
            return;
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FileName);
            if (File.Exists(path))
                File.Move(path, Path.Combine(folder, PreviousFileName), overwrite: true);
            _logFile = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never stop the app from starting; the console still works.
        }
    }

    public void Log(LogLevel level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level,11}] {message}";
        lock (_lock)
        {
            Console.WriteLine(line);
            if (_logFile is null)
                return;
            try
            {
                File.AppendAllText(_logFile, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void Log(LogLevel level, string message, Exception exception) =>
        Log(level, $"{message}\n{exception}");

    public void Debug(string message) => Log(LogLevel.Debug, message);
    public void Information(string message) => Log(LogLevel.Information, message);
    public void Warning(string message) => Log(LogLevel.Warning, message);
    public void Error(string message) => Log(LogLevel.Error, message);
    public void Error(string message, Exception exception) => Log(LogLevel.Error, message, exception);
}
