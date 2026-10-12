using Avalonia.Input;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Core;

/// <summary>
/// The key the game's scoreboard is held on, which the advisor watches (it never takes the key, as Detect's
/// is taken: the game has to keep receiving it). Tab unless the player moved it in the game.
/// </summary>
public static class ScoreboardKeys
{
    public const Key Default = Key.Tab;

    /// <summary>Keys the advisor can tell are held: Tab and Caps Lock, plus anything a shortcut can be on.</summary>
    public static bool IsSupported(Key key) => key is Key.Tab or Key.CapsLock || ShortcutKeys.IsSupported(key);

    /// <summary>Why <paramref name="gesture"/> can't be the scoreboard key, or null if it can.</summary>
    public static string? Problem(KeyGesture gesture)
    {
        if (gesture.KeyModifiers != KeyModifiers.None)
            return "The scoreboard key is watched on its own: pick the key without Ctrl, Alt, Shift or the Windows key.";
        return IsSupported(gesture.Key) ? null : $"{Label(gesture.Key)} can't be the scoreboard key: use Tab, Caps Lock, a function key, a letter or a number.";
    }

    /// <summary>The scoreboard key, falling back to Tab for a setting that names nothing usable.</summary>
    public static Key Get(this AppSettings settings) =>
        Enum.TryParse<Key>(settings.ScoreboardKey, out var key) && IsSupported(key) ? key : Default;

    public static string Label(Key key) => ShortcutKeys.Label(new KeyGesture(key));

    /// <summary>The Windows virtual-key code <see cref="GlobalHotkeyService.VirtualKey"/> knows, plus Tab and Caps Lock.</summary>
    public static uint? VirtualKey(Key key) => key switch
    {
        Key.Tab => 0x09,
        Key.CapsLock => 0x14,
        _ => GlobalHotkeyService.VirtualKey(key),
    };
}
