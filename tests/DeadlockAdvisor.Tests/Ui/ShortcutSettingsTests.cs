using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Settings;
using DeadlockAdvisor.Features.Settings.Shortcuts;

namespace DeadlockAdvisor.Tests.Ui;

public class ShortcutSettingsTests
{
    [AvaloniaFact]
    public void ClickingAKeyAndPressingANewOneMovesIt()
    {
        using var ui = OpenShortcuts();
        var recorder = RecorderFor(ui, "Random");
        Assert.Equal("F6", recorder.Content);
        ui.Screenshot("settings_shortcuts.png");

        Click(ui.Window, recorder);
        Assert.True(recorder.IsRecording);
        Assert.Equal("Press a key…", recorder.Content);
        ui.Screenshot("settings_shortcuts_recording.png");
        // Detect's key is held system-wide, and pressing it here must reach the recorder instead.
        Assert.True(ui.Hotkey.IsSuspended);

        ui.Window.KeyPressQwerty(PhysicalKey.G, RawInputModifiers.Control | RawInputModifiers.Shift);
        UiHarness.Settle();

        Assert.False(recorder.IsRecording);
        Assert.False(ui.Hotkey.IsSuspended);
        Assert.Equal(new KeyGesture(Key.G, KeyModifiers.Control | KeyModifiers.Shift), ui.Settings.Current.Gesture(ShortcutAction.Randomize));
        Assert.Equal("Ctrl+Shift+G", recorder.Content);
        Assert.Contains(ui.ViewModel.ShortcutBindings, binding => binding.Gesture.Equals(new KeyGesture(Key.G, KeyModifiers.Control | KeyModifiers.Shift)));

        // Reset puts it back.
        Click(ui.Window, Row(ui, "Random").GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Reset")));
        Assert.Equal(new KeyGesture(Key.F6), ui.Settings.Current.Gesture(ShortcutAction.Randomize));
        Assert.Empty(ui.Settings.Current.Shortcuts);
    }

    [AvaloniaFact]
    public void TakingAnotherShortcutsKeySwapsTheTwo()
    {
        using var ui = OpenShortcuts();

        Record(ui, "Detect from screen", PhysicalKey.F6);

        Assert.Equal(new KeyGesture(Key.F6), ui.Settings.Current.Gesture(ShortcutAction.Detect));
        Assert.Equal(new KeyGesture(Key.F9), ui.Settings.Current.Gesture(ShortcutAction.Randomize));
        Assert.Equal("Taken from Random, which moved to F9.", ui.ViewModel.Settings.Shortcuts.Detect.Description);
        Assert.Equal("F9", RecorderFor(ui, "Random").Content);
    }

    [AvaloniaFact]
    public void AKeyThatCantBeAShortcutIsTurnedAwayWithTheReason()
    {
        using var ui = OpenShortcuts();
        var row = ui.ViewModel.Settings.Shortcuts.Randomize;

        Record(ui, "Random", PhysicalKey.F, RawInputModifiers.Control);
        // Pressed to record, the window's own Ctrl+F doesn't also run.
        Assert.True(ui.ViewModel.IsSettingsOpen);
        Assert.Equal("Ctrl+F is already Find.", row.Description);

        Record(ui, "Random", PhysicalKey.A);
        Assert.Contains("A is for typing", row.Description);

        Assert.Equal(new KeyGesture(Key.F6), ui.Settings.Current.Gesture(ShortcutAction.Randomize));
        Assert.Empty(ui.Settings.Current.Shortcuts);

        // The reason goes once there's a new key to record.
        Click(ui.Window, RecorderFor(ui, "Random"));
        Assert.StartsWith("Fill the match", row.Description);
    }

    [AvaloniaFact]
    public void DeleteTakesTheKeyAwayAndEscapeKeepsItWithoutClosingSettings()
    {
        using var ui = OpenShortcuts();

        Record(ui, "Random, keeping your hero", PhysicalKey.Escape);
        Assert.True(ui.ViewModel.IsSettingsOpen);
        Assert.Equal(new KeyGesture(Key.F7), ui.Settings.Current.Gesture(ShortcutAction.RandomizeKeepSelf));

        Record(ui, "Random, keeping your hero", PhysicalKey.Delete);
        Assert.Null(ui.Settings.Current.Gesture(ShortcutAction.RandomizeKeepSelf));
        Assert.Equal("None", RecorderFor(ui, "Random, keeping your hero").Content);
        Assert.DoesNotContain(ui.ViewModel.ShortcutBindings, binding => binding.Gesture.Equals(new KeyGesture(Key.F7)));

        ui.ViewModel.Settings.Shortcuts.ResetAllCommand.Execute().Subscribe();
        UiHarness.Settle();
        Assert.Equal(new KeyGesture(Key.F7), ui.Settings.Current.Gesture(ShortcutAction.RandomizeKeepSelf));
    }

    private static UiHarness OpenShortcuts()
    {
        var ui = new UiHarness();
        ui.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        var settings = ui.ViewModel.Settings;
        settings.SelectedCategory = settings.Categories.Single(category => category.Page is ShortcutsSettingsViewModel);
        ui.Show();
        return ui;
    }

    private static void Record(UiHarness ui, string title, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Click(ui.Window, RecorderFor(ui, title));
        ui.Window.KeyPressQwerty(key, modifiers);
        UiHarness.Settle();
    }

    private static SettingRow Row(UiHarness ui, string title) =>
        ui.Window.SettingsPage.GetVisualDescendants().OfType<SettingRow>().Single(row => row.Title == title);

    private static ShortcutRecorder RecorderFor(UiHarness ui, string title) =>
        Row(ui, title).GetVisualDescendants().OfType<ShortcutRecorder>().Single();

    private static void Click(TopLevel root, Visual target)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root)!.Value;
        root.MouseDown(at, MouseButton.Left);
        root.MouseUp(at, MouseButton.Left);
        UiHarness.Settle();
    }
}
