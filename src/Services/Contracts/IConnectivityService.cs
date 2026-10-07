using System.Reactive;

namespace DeadlockAdvisor.Services.Contracts;

public enum ConnectivityState
{
    Online,
    Offline,

    /// <summary>Offline, with a check the user asked for under way.</summary>
    Checking,
}

/// <summary>
/// Whether the app can reach the internet, as the requests it makes find out: it's offline once every
/// server it has asked has failed to answer, and back once one does. While offline it checks again by itself.
/// </summary>
public interface IConnectivityService
{
    ConnectivityState State { get; }

    bool IsOffline => State != ConnectivityState.Online;

    /// <summary>The state now, then each change, from whichever thread saw it.</summary>
    IObservable<ConnectivityState> States { get; }

    /// <summary>The connection is back after being down: what the time offline skipped can be caught up on.</summary>
    IObservable<Unit> Reconnected { get; }

    /// <summary>Check for a connection now, rather than at the next timed check. Does nothing while online.</summary>
    void Retry();
}
