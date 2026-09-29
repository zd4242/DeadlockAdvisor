using System.Reactive;

namespace DeadlockAdvisor.Services.Contracts;

public enum HotkeyStatus
{
    /// <summary>Turned off in Settings: F9 only works while this window has focus.</summary>
    Off,

    /// <summary>F9 reaches the app whichever window has focus.</summary>
    Registered,

    /// <summary>Another app already holds F9 system-wide.</summary>
    Taken,

    /// <summary>Not on Windows, or no native window to register it against (as in tests).</summary>
    Unsupported,
}

/// <summary>F9 for Detect while another app, such as the game, has focus, as Settings → Detection allows.</summary>
public interface IGlobalHotkeyService
{
    /// <summary>F9 was pressed, in this app or any other, on the UI thread.</summary>
    IObservable<Unit> Pressed { get; }

    /// <summary>Whether F9 is held, starting with the current status.</summary>
    IObservable<HotkeyStatus> Status { get; }

    /// <summary>Register F9 against <paramref name="window"/>, and follow the setting from then on until it closes.</summary>
    void Attach(TopLevel window);
}
