using System.Reactive;

namespace DeadlockAdvisor.Services.Contracts;

public enum HotkeyStatus
{
    /// <summary>Turned off in Settings, or Detect has no key: it only works while this window has focus.</summary>
    Off,

    /// <summary>The key reaches the app whichever window has focus.</summary>
    Registered,

    /// <summary>Another app already holds the key system-wide.</summary>
    Taken,

    /// <summary>Not on Windows, or no native window to register it against (as in tests).</summary>
    Unsupported,
}

/// <summary>Detect's key (F9 unless rebound) while another app, such as the game, has focus, as Settings → Detection allows.</summary>
public interface IGlobalHotkeyService
{
    /// <summary>The key was pressed, in this app or any other, on the UI thread.</summary>
    IObservable<Unit> Pressed { get; }

    /// <summary>Whether the key is held, starting with the current status.</summary>
    IObservable<HotkeyStatus> Status { get; }

    /// <summary>Register the key against <paramref name="window"/>, and follow the settings from then on until it closes.</summary>
    void Attach(TopLevel window);

    /// <summary>Let go of the key until the result is disposed, so pressing it while rebinding reaches the window rather than detecting.</summary>
    IDisposable Suspend();
}
