using System.Reactive;
using System.Reactive.Subjects;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Tests.Fakes;

/// <summary>A connection a test turns off and on: online until it says otherwise.</summary>
public sealed class FakeConnectivity : IConnectivityService
{
    private readonly BehaviorSubject<ConnectivityState> _states = new(ConnectivityState.Online);
    private readonly Subject<Unit> _reconnected = new();

    public ConnectivityState State => _states.Value;
    public IObservable<ConnectivityState> States => _states;
    public IObservable<Unit> Reconnected => _reconnected;

    /// <summary>How many times someone asked to check the connection.</summary>
    public int Retries { get; private set; }

    public void Retry() => Retries++;

    public void GoOffline() => _states.OnNext(ConnectivityState.Offline);

    public void StartChecking() => _states.OnNext(ConnectivityState.Checking);

    /// <summary>The connection returns, as the service announces it.</summary>
    public void Reconnect()
    {
        _states.OnNext(ConnectivityState.Online);
        _reconnected.OnNext(Unit.Default);
    }
}
