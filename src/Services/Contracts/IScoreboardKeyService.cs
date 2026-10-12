using System.Reactive;

namespace DeadlockAdvisor.Services.Contracts;

public enum ScoreboardKeyStatus
{
    /// <summary>Reading builds on the scoreboard key is off in Settings, so nothing is watched.</summary>
    Off,

    /// <summary>On: the key is looked at while the game has focus.</summary>
    Watching,

    /// <summary>Not on Windows, so the key can't be watched.</summary>
    Unsupported,
}

/// <summary>
/// The game's scoreboard key (Tab unless moved) going down while the game has focus. Unlike Detect's key it isn't
/// registered with Windows, which would keep it from the game: it is looked at, a few times a second, and only while
/// the setting is on and the game is in front.
/// </summary>
public interface IScoreboardKeyService
{
    /// <summary>The key went down (one signal per press, not per repeat), on the UI thread.</summary>
    IObservable<Unit> Pressed { get; }

    IObservable<ScoreboardKeyStatus> Status { get; }

    /// <summary>Whether the key is down right now.</summary>
    bool IsHeld { get; }
}
