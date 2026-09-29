using Avalonia.Input;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

public class ShortcutKeysTests
{
    [Fact]
    public void TheDefaultsAreF6ToF9AndAllowed()
    {
        var settings = new AppSettings();

        Assert.Equal(new KeyGesture(Key.F9), settings.Gesture(ShortcutAction.Detect));
        Assert.Equal(new KeyGesture(Key.F6), settings.Gesture(ShortcutAction.Randomize));
        Assert.Equal(new KeyGesture(Key.F7), settings.Gesture(ShortcutAction.RandomizeKeepSelf));
        Assert.Equal(new KeyGesture(Key.F8), settings.Gesture(ShortcutAction.RandomizeKeepTeam));
        Assert.All(ShortcutKeys.Defaults.Values, gesture => Assert.Null(ShortcutKeys.Problem(gesture)));
    }

    /// <summary>Only moved keys are saved, so a key put back on its default follows the default from then on.</summary>
    [Fact]
    public async Task MovedAndRemovedKeysRoundTripThroughTheSettingsFile()
    {
        using var folder = new TempDirectory();
        var service = new JsonSettingsService(new FakeLoggingService(), folder.Path);
        service.Update(s =>
        {
            s.SetGesture(ShortcutAction.Detect, new KeyGesture(Key.D1, KeyModifiers.Control | KeyModifiers.Shift));
            s.SetGesture(ShortcutAction.Randomize, null);
            s.SetGesture(ShortcutAction.RandomizeKeepSelf, new KeyGesture(Key.PageUp, KeyModifiers.Alt));
            s.SetGesture(ShortcutAction.RandomizeKeepTeam, new KeyGesture(Key.F8));
        });
        Assert.Equal(3, service.Current.Shortcuts.Count);
        Assert.Equal("Ctrl+Shift+D1", service.Current.Shortcuts[ShortcutAction.Detect]);
        for (var attempt = 0; attempt < 100 && !File.Exists(folder.File("settings.json")); attempt++)
            await Task.Delay(20);

        var reloaded = new JsonSettingsService(new FakeLoggingService(), folder.Path);
        await reloaded.LoadAsync();

        var settings = reloaded.Current;
        Assert.Equal(new KeyGesture(Key.D1, KeyModifiers.Control | KeyModifiers.Shift), settings.Gesture(ShortcutAction.Detect));
        Assert.Null(settings.Gesture(ShortcutAction.Randomize));
        Assert.Equal(new KeyGesture(Key.PageUp, KeyModifiers.Alt), settings.Gesture(ShortcutAction.RandomizeKeepSelf));
        Assert.Equal(new KeyGesture(Key.F8), settings.Gesture(ShortcutAction.RandomizeKeepTeam));
    }

    [Theory]
    [InlineData("Banana")]
    [InlineData("Ctrl+F")]
    [InlineData("J")]
    public void ASavedKeyThatIsntAllowedFallsBackToTheDefault(string saved)
    {
        var settings = new AppSettings { Shortcuts = { [ShortcutAction.Detect] = saved } };

        Assert.Equal(new KeyGesture(Key.F9), settings.Gesture(ShortcutAction.Detect));
    }

    [Theory]
    [InlineData(Key.F5, KeyModifiers.None)]
    [InlineData(Key.F5, KeyModifiers.Shift)]
    [InlineData(Key.G, KeyModifiers.Control)]
    [InlineData(Key.D5, KeyModifiers.Alt | KeyModifiers.Shift)]
    [InlineData(Key.NumPad3, KeyModifiers.Control)]
    [InlineData(Key.Pause, KeyModifiers.None)]
    public void KeysThatCantTypeOrClashAreAllowed(Key key, KeyModifiers modifiers)
    {
        Assert.Null(ShortcutKeys.Problem(new KeyGesture(key, modifiers)));
    }

    [Theory]
    [InlineData(Key.A, KeyModifiers.None, "A is for typing")]
    [InlineData(Key.D4, KeyModifiers.Shift, "Shift+4 is for typing")]
    [InlineData(Key.F, KeyModifiers.Control, "Ctrl+F is already Find")]
    [InlineData(Key.D1, KeyModifiers.Alt, "Alt+1 is already picking enemies")]
    [InlineData(Key.D, KeyModifiers.Alt, "Alt+D is already the Data menu")]
    [InlineData(Key.F12, KeyModifiers.None, "F12")]
    [InlineData(Key.Enter, KeyModifiers.Control, "can't be a shortcut")]
    [InlineData(Key.F5, KeyModifiers.Meta, "Windows key")]
    public void KeysThatCantBeShortcutsSayWhy(Key key, KeyModifiers modifiers, string reason)
    {
        Assert.Contains(reason, ShortcutKeys.Problem(new KeyGesture(key, modifiers)));
    }

    [Fact]
    public void LabelsReadTheWayTheKeysArePrinted()
    {
        Assert.Equal("F9", ShortcutKeys.Label(new KeyGesture(Key.F9)));
        Assert.Equal("Ctrl+Shift+1", ShortcutKeys.Label(new KeyGesture(Key.D1, KeyModifiers.Control | KeyModifiers.Shift)));
        Assert.Equal("Ctrl+Alt+Num 5", ShortcutKeys.Label(new KeyGesture(Key.NumPad5, KeyModifiers.Alt | KeyModifiers.Control)));
        Assert.Equal("Alt+Page Up", ShortcutKeys.Label(new KeyGesture(Key.PageUp, KeyModifiers.Alt)));
    }

    /// <summary>Detect's key is held system-wide, so every key a shortcut can be on needs a Windows key code.</summary>
    [Fact]
    public void EveryAllowedKeyHasAWindowsKeyCode()
    {
        Assert.All(Enum.GetValues<Key>().Where(ShortcutKeys.IsSupported), key => Assert.NotNull(GlobalHotkeyService.VirtualKey(key)));
        Assert.Equal(0x78u, GlobalHotkeyService.VirtualKey(Key.F9));
        Assert.Equal(0x41u, GlobalHotkeyService.VirtualKey(Key.A));
        Assert.Equal(0x31u, GlobalHotkeyService.VirtualKey(Key.D1));
        Assert.Equal(0x65u, GlobalHotkeyService.VirtualKey(Key.NumPad5));
        Assert.Equal(0x21u, GlobalHotkeyService.VirtualKey(Key.PageUp));
        Assert.Equal(0x0006u, GlobalHotkeyService.Modifiers(KeyModifiers.Control | KeyModifiers.Shift));
    }
}
