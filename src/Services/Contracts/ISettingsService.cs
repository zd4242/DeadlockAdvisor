using DeadlockAdvisor.Models;

namespace DeadlockAdvisor.Services.Contracts;

public interface ISettingsService
{
    AppSettings Current { get; }
    IObservable<AppSettings> SettingsChanged { get; }
    void Update(Action<AppSettings> mutate);
    Task LoadAsync();
}
