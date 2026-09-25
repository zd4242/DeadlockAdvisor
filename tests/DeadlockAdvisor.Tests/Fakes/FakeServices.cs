using System.Reactive.Subjects;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Tests.Fakes;

public sealed class FakeSettingsService : ISettingsService
{
    private readonly BehaviorSubject<AppSettings> _settings = new(new AppSettings());

    public AppSettings Current => _settings.Value;
    public IObservable<AppSettings> SettingsChanged => _settings;

    public void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        _settings.OnNext(Current);
    }

    public Task LoadAsync() => Task.CompletedTask;
}

public sealed class FakeLoggingService : ILoggingService
{
    public void Log(LogLevel level, string message) { }
    public void Log(LogLevel level, string message, Exception exception) { }
    public void Debug(string message) { }
    public void Information(string message) { }
    public void Warning(string message) { }
    public void Error(string message) { }
    public void Error(string message, Exception exception) { }
}
