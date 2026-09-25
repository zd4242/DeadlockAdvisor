using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

public class ConsoleLoggingService : ILoggingService
{
    private readonly object _lock = new();

    public void Log(LogLevel level, string message)
    {
        lock (_lock)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            Console.WriteLine($"[{timestamp}] [{level,11}] {message}");
        }
    }

    public void Log(LogLevel level, string message, Exception exception)
    {
        Log(level, $"{message}\nException: {exception.GetType().Name}\nMessage: {exception.Message}\nStack Trace: {exception.StackTrace}");
    }

    public void Debug(string message) => Log(LogLevel.Debug, message);
    public void Information(string message) => Log(LogLevel.Information, message);
    public void Warning(string message) => Log(LogLevel.Warning, message);
    public void Error(string message) => Log(LogLevel.Error, message);
    public void Error(string message, Exception exception) => Log(LogLevel.Error, message, exception);
}
