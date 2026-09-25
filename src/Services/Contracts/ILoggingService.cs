namespace DeadlockAdvisor.Services.Contracts;

public enum LogLevel
{
    Debug,
    Information,
    Warning,
    Error
}

public interface ILoggingService
{
    void Log(LogLevel level, string message);
    void Log(LogLevel level, string message, Exception exception);

    void Debug(string message);
    void Information(string message);
    void Warning(string message);
    void Error(string message);
    void Error(string message, Exception exception);
}
