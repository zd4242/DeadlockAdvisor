using Avalonia.Input;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;

namespace DeadlockAdvisor.Core;

/// <summary>
/// The Match page's rebindable keys. Settings keep only the ones changed from <see cref="Defaults"/>,
/// as "Ctrl+Shift+F9", with "" for one taken away, so a new default reaches everyone who hasn't
/// changed it.
/// </summary>
public static class ShortcutKeys
{
    public static readonly IReadOnlyDictionary<ShortcutAction, KeyGesture> Defaults = new Dictionary<ShortcutAction, KeyGesture>
    {
        [ShortcutAction.Detect] = new(Key.F9),
        [ShortcutAction.Randomize] = new(Key.F6),
        [ShortcutAction.RandomizeKeepSelf] = new(Key.F7),
        [ShortcutAction.RandomizeKeepTeam] = new(Key.F8),
    };

    /// <summary>The window's fixed keys (and the menus' access keys) that a key allowed here could clash with.</summary>
    private static readonly Dictionary<(Key, KeyModifiers), string> _reserved = new()
    {
        [(Key.D0, KeyModifiers.Control)] = "Reset Zoom",
        [(Key.NumPad0, KeyModifiers.Control)] = "Reset Zoom",
        [(Key.R, KeyModifiers.Control)] = "Reload from Disk",
        [(Key.F, KeyModifiers.Control)] = "Find",
        [(Key.Q, KeyModifiers.Control)] = "Quit",
        [(Key.D1, KeyModifiers.Alt)] = "picking your hero",
        [(Key.D2, KeyModifiers.Alt)] = "picking enemies",
        [(Key.D3, KeyModifiers.Alt)] = "picking allies",
        [(Key.D, KeyModifiers.Alt)] = "the Data menu",
        [(Key.V, KeyModifiers.Alt)] = "the View menu",
        [(Key.H, KeyModifiers.Alt)] = "the Help menu",
        [(Key.F4, KeyModifiers.Alt)] = "closing the window",
    };

    /// <summary>The key <paramref name="action"/> is on, or null if it's been taken away.</summary>
    public static KeyGesture? Gesture(this AppSettings settings, ShortcutAction action)
    {
        if (!settings.Shortcuts.TryGetValue(action, out var saved))
            return Defaults[action];
        if (saved.Length == 0)
            return null;
        return Parse(saved) is { } gesture && Problem(gesture) is null ? gesture : Defaults[action];
    }

    public static void SetGesture(this AppSettings settings, ShortcutAction action, KeyGesture? gesture)
    {
        if (Equals(gesture, Defaults[action]))
            settings.Shortcuts.Remove(action);
        else
            settings.Shortcuts[action] = gesture is null ? "" : Format(gesture);
    }

    /// <summary>How the app writes a key for people: "Ctrl+Shift+F9", "Alt+1", "Num 5".</summary>
    public static string Label(KeyGesture gesture) => ModifierPrefix(gesture.KeyModifiers) + KeyName(gesture.Key);

    /// <summary><paramref name="text"/> with its key after it in brackets, as a button or tooltip names it: "Random (F6)".</summary>
    public static string WithKey(string text, KeyGesture? gesture) => gesture is null ? text : $"{text} ({Label(gesture)})";

    /// <summary>Why <paramref name="gesture"/> can't be a shortcut, or null if it can.</summary>
    public static string? Problem(KeyGesture gesture)
    {
        var label = Label(gesture);
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta))
            return "Shortcuts with the Windows key belong to Windows: use Ctrl, Alt or Shift.";
        if (!IsSupported(gesture.Key))
            return $"{KeyName(gesture.Key)} can't be a shortcut: use a function key, or a letter or number with Ctrl or Alt.";
        if (gesture.Key == Key.F12)
            return "Windows keeps F12 for debuggers.";
        if (_reserved.TryGetValue((gesture.Key, gesture.KeyModifiers), out var taken))
            return $"{label} is already {taken}.";
        if ((gesture.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == 0 && !TypesNothing(gesture.Key))
            return $"{label} is for typing: hold Ctrl or Alt with it, or use a function key.";
        return null;
    }

    /// <summary>The keys a shortcut can be on, all of which Windows can also hold system-wide.</summary>
    public static bool IsSupported(Key key) => key is >= Key.F1 and <= Key.F24
        or >= Key.A and <= Key.Z
        or >= Key.D0 and <= Key.D9
        or >= Key.NumPad0 and <= Key.NumPad9
        or Key.Insert or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Pause or Key.Scroll;

    /// <summary>Keys that neither type nor move the caret, so they're safe without Ctrl or Alt even while a text box has focus.</summary>
    private static bool TypesNothing(Key key) => key is >= Key.F1 and <= Key.F24 or Key.Pause or Key.Scroll;

    private static KeyGesture? Parse(string text)
    {
        try
        {
            return KeyGesture.Parse(text);
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>What <see cref="KeyGesture.Parse"/> reads back: modifiers and the key by their enum names.</summary>
    private static string Format(KeyGesture gesture) => ModifierPrefix(gesture.KeyModifiers) + gesture.Key;

    private static string ModifierPrefix(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Control) ? "Ctrl+" : "")
        + (modifiers.HasFlag(KeyModifiers.Shift) ? "Shift+" : "")
        + (modifiers.HasFlag(KeyModifiers.Alt) ? "Alt+" : "")
        + (modifiers.HasFlag(KeyModifiers.Meta) ? "Win+" : "");

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => $"{key - Key.D0}",
        >= Key.NumPad0 and <= Key.NumPad9 => $"Num {key - Key.NumPad0}",
        Key.PageUp => "Page Up",
        Key.PageDown => "Page Down",
        Key.Scroll => "Scroll Lock",
        _ => key.ToString(),
    };
}
