using System.Reactive.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Settings;
using DeadlockAdvisor.Features.Settings.Data;
using DeadlockAdvisor.Features.Settings.Detection;
using DeadlockAdvisor.Features.Settings.General;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services.Contracts;
using static DeadlockAdvisor.Tests.Support.VisionData;

namespace DeadlockAdvisor.Tests.Ui;

public class SettingsPageTests
{
    [AvaloniaFact]
    public void TheGearOpensSettingsOverThePagesAndATabClosesIt()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings, 2));
        ui.Show();

        Click(ui.Window, ui.Window.FindControl<Button>("SettingsButton")!);

        Assert.True(ui.ViewModel.IsSettingsOpen);
        Assert.True(ui.Window.SettingsPage.IsEffectivelyVisible);
        Assert.False(ui.Window.HeroTraitsPage.IsEffectivelyVisible);
        Assert.Equal(-1, ui.Window.PageTabs.SelectedIndex);
        Assert.IsType<GeneralSettingsView>(ui.Window.SettingsPage.GetVisualDescendants().OfType<GeneralSettingsView>().Single());
        ui.Screenshot("settings_general.png");

        // The page it was opened from is a tab too: picking it goes back there.
        Click(ui.Window, TabItem(ui, "Hero Traits"));

        Assert.False(ui.ViewModel.IsSettingsOpen);
        Assert.True(ui.ViewModel.IsHeroTraitsPage);
        Assert.Equal(2, ui.Window.PageTabs.SelectedIndex);
    }

    [AvaloniaFact]
    public void CtrlCommaOpensItAndEscapeOrBackCloseIt()
    {
        using var ui = new UiHarness();
        ui.Show();

        ui.Window.KeyPressQwerty(PhysicalKey.Comma, RawInputModifiers.Control);
        UiHarness.Settle();
        Assert.True(ui.ViewModel.IsSettingsOpen);
        Assert.True(ui.Window.SettingsPage.IsKeyboardFocusWithin);

        ui.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.False(ui.ViewModel.IsSettingsOpen);
        Assert.True(ui.ViewModel.IsMatchPage);

        ui.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        UiHarness.Settle();
        Click(ui.Window, ui.Window.SettingsPage.FindControl<Button>("BackButton")!);
        Assert.False(ui.ViewModel.IsSettingsOpen);
    }

    [AvaloniaFact]
    public void EachCategoryShowsItsOwnPage()
    {
        using var ui = new UiHarness(settings => settings.Current.VisionGeometry["2560x1440"] = new JsonObject { ["version"] = 2 });
        ui.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        ui.Show();
        var settings = ui.ViewModel.Settings;

        settings.SelectedCategory = settings.Categories.Single(category => category.Page is DetectionSettingsViewModel);
        UiHarness.Settle();
        Assert.Single(ui.Window.SettingsPage.GetVisualDescendants().OfType<DetectionSettingsView>());
        Assert.Contains("Remembered for 2560 × 1440.", settings.Detection.RememberedLayouts);
        ui.Screenshot("settings_detection.png");

        settings.SelectedCategory = settings.Categories.Single(category => category.Page is DataSettingsViewModel);
        UiHarness.Settle();
        Assert.Single(ui.Window.SettingsPage.GetVisualDescendants().OfType<DataSettingsView>());
        Assert.Equal(ui.Data.DataRoot, settings.Data.DataFolder);
        ui.Screenshot("settings_data.png");
    }

    [AvaloniaFact]
    public void ASwitchSavesAsItsFlipped()
    {
        using var ui = new UiHarness();
        ui.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        ui.Show();
        var row = ui.Window.SettingsPage.GetVisualDescendants().OfType<SettingRow>().Single(r => r.Title == "Reopen the last match");

        Click(ui.Window, row.GetVisualDescendants().OfType<ToggleSwitch>().Single());

        Assert.False(ui.Settings.Current.ReopenLastMatch);
        Assert.False(ui.ViewModel.Settings.General.ReopenLastMatch);
    }

    [AvaloniaFact]
    public void TheUpdateSelectorsPickAModeAndSayWhatItDoes()
    {
        using var ui = new UiHarness();
        ui.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        var settings = ui.ViewModel.Settings;
        settings.SelectedCategory = settings.Categories.Single(category => category.Page is DataSettingsViewModel);
        ui.Show();
        var data = settings.Data;
        Assert.Equal(UpdateMode.Automatic, data.MatchDataMode.Mode);
        Assert.Equal(["Automatic", "Tell me", "Off"], SegmentsOf(ui, "Keep match results up to date"));
        Assert.Equal(["Automatic", "Tell me", "Off"], SegmentsOf(ui, "Keep the hero ratings and item formulas up to date"));
        Assert.Equal(["Tell me", "Off"], SegmentsOf(ui, "Say when a new version is out"));
        ui.Screenshot("settings_data_modes.png");

        Click(ui.Window, SegmentOf(ui, "Keep match results up to date", "Tell me"));

        Assert.False(ui.Settings.Current.AutoUpdateMatchData);
        Assert.True(ui.Settings.Current.CheckForNewerPatch);
        Assert.Equal(data.MatchDataModes[1].Description, RowTitled(ui, "Keep match results up to date").Description);

        Click(ui.Window, SegmentOf(ui, "Keep the hero ratings and item formulas up to date", "Off"));

        Assert.False(ui.Settings.Current.AutoUpdateModel);
        Assert.False(ui.Settings.Current.CheckForNewHeroes);
        Assert.True(ui.Settings.Current.CheckForNewerPatch);

        Click(ui.Window, SegmentOf(ui, "Say when a new version is out", "Off"));

        Assert.False(ui.Settings.Current.CheckForAppUpdates);
        ui.Screenshot("settings_data_modes_changed.png");
    }

    /// <summary>One check for everything is the first thing on the page, and it is the flyout's and the menu's.</summary>
    [AvaloniaFact]
    public void TheDataPageStartsWithTheOneCheckForEverything()
    {
        using var ui = new UiHarness();
        ui.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        var settings = ui.ViewModel.Settings;
        settings.ShowData();
        ui.Show();

        Assert.Same(ui.ViewModel.Updates.CheckAllCommand, settings.Data.CheckForUpdatesCommand);
        var button = ui.Window.SettingsPage.GetVisualDescendants().OfType<Button>().Single(candidate => Equals(candidate.Content, "Check for updates"));
        Assert.Same(settings.Data.CheckForUpdatesCommand, button.Command);
        var firstSection = ui.Window.SettingsPage.GetVisualDescendants().OfType<DataSettingsView>().Single()
            .GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("section"));
        Assert.Equal("UPDATES", firstSection.Text);
    }

    [AvaloniaFact]
    public void TheSelectorsFollowSettingsChangedElsewhere()
    {
        using var ui = new UiHarness();
        var data = ui.ViewModel.Settings.Data;
        Assert.Equal(UpdateMode.Automatic, data.MatchDataMode.Mode);

        // The first-run offer turns updates off.
        ui.Settings.Update(s => s.AutoUpdateMatchData = false);
        data.Refresh();

        Assert.Equal(UpdateMode.TellMe, data.MatchDataMode.Mode);
        Assert.Equal(data.MatchDataModes[1].Description, data.MatchDataModeDescription);
    }

    [AvaloniaFact]
    public void TheZoomStepperZoomsTheWindow()
    {
        using var ui = new UiHarness();
        ui.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        ui.Show();
        var general = ui.ViewModel.Settings.General;
        Assert.Equal("100%", general.ZoomText);
        Assert.False(general.ResetZoomCommand.CanExecute(null));

        var zoomIn = ui.Window.SettingsPage.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "+"));
        Click(ui.Window, zoomIn);

        Assert.Equal("115%", general.ZoomText);
        Assert.Equal(1.15, ui.ViewModel.UiScale);
        Assert.True(general.ResetZoomCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void StartupCanIgnoreTheLastPageAndMatch()
    {
        var match = new MatchState();
        match.SetRole("haze", Role.Enemy);
        using var ui = new UiHarness(settings =>
        {
            UiHarness.Editing(settings, 2);
            settings.Current.LastMatch = match.ToSaved();
            settings.Current.ReopenLastPage = false;
            settings.Current.ReopenLastMatch = false;
        });

        Assert.True(ui.ViewModel.IsMatchPage);
        Assert.Empty(ui.ViewModel.Match.Match.RoleMap);
    }

    [AvaloniaFact]
    public void TheModelEditorsStayHiddenUntilTurnedOn()
    {
        // Last on Hero Traits, before the editors were hidden.
        using var ui = new UiHarness(settings => settings.Current.LastPage = 2);
        ui.Show();

        Assert.True(ui.ViewModel.IsMatchPage);
        Assert.Equal(["Match", "Hero Items"], TabNames(ui));
        ui.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
        UiHarness.Settle();
        Assert.True(ui.ViewModel.IsHeroItemsPage);
        ui.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
        UiHarness.Settle();
        Assert.True(ui.ViewModel.IsMatchPage);

        ui.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        UiHarness.Settle();
        Click(ui.Window, RowTitled(ui, "Edit the scoring model").GetVisualDescendants().OfType<ToggleSwitch>().Single());
        Assert.True(ui.Settings.Current.ShowModelEditors);
        Assert.Equal(["Match", "Hero Items", "Hero Traits", "Item Formulas"], TabNames(ui));
        Click(ui.Window, TabItem(ui, "Hero Traits"));
        Assert.True(ui.ViewModel.IsHeroTraitsPage);
        Assert.Equal(2, ui.Window.PageTabs.SelectedIndex);

        // Turned off from Settings, which then closes onto Match rather than the page that's gone.
        ui.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        ui.ViewModel.Settings.General.ShowModelEditors = false;
        ui.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.True(ui.ViewModel.IsMatchPage);
        Assert.Equal(["Match", "Hero Items"], TabNames(ui));
        Assert.Equal(0, ui.Window.PageTabs.SelectedIndex);
    }

    [AvaloniaFact]
    public void ForgettingTheHeroStripClearsEveryScreenSize()
    {
        using var ui = new UiHarness(settings =>
        {
            settings.Current.VisionGeometry["2560x1440"] = new JsonObject { ["version"] = 2 };
            settings.Current.VisionGeometry["1920x1080"] = new JsonObject { ["version"] = 2 };
        });
        var detection = ui.ViewModel.Settings.Detection;
        Assert.Contains("Remembered for 1920 × 1080, 2560 × 1440.", detection.RememberedLayouts);

        detection.ForgetLayoutsCommand.Execute().Subscribe();

        Assert.Empty(ui.Settings.Current.VisionGeometry);
        Assert.Contains("None remembered yet.", detection.RememberedLayouts);
        Assert.False(((System.Windows.Input.ICommand)detection.ForgetLayoutsCommand).CanExecute(null));
    }

    [AvaloniaFact]
    public void TheDetectFromAnywhereSettingNamesTheKeyAndSaysWhenAnotherAppHasIt()
    {
        using var ui = new UiHarness();
        var detection = ui.ViewModel.Settings.Detection;
        Assert.True(detection.DetectFromAnywhere);
        Assert.StartsWith("Press F9 in the game", detection.DetectFromAnywhereDescription);
        Assert.DoesNotContain("Another app", detection.DetectFromAnywhereDescription);

        ui.Hotkey.SetStatus(HotkeyStatus.Taken);
        Assert.Contains("Another app already has F9", detection.DetectFromAnywhereDescription);

        ui.Settings.Update(s => s.SetGesture(ShortcutAction.Detect, new KeyGesture(Key.G, KeyModifiers.Control | KeyModifiers.Shift)));
        Assert.Contains("Another app already has Ctrl+Shift+G", detection.DetectFromAnywhereDescription);
        ui.Settings.Update(s => s.SetGesture(ShortcutAction.Detect, null));
        Assert.Contains("has no key", detection.DetectFromAnywhereDescription);

        detection.DetectFromAnywhere = false;
        Assert.False(ui.Settings.Current.DetectFromAnywhere);
    }

    [AvaloniaFact]
    public async Task DetectLeavesTheWindowUpWhenAskedTo()
    {
        using var ui = new UiHarness(settings => settings.Current.MinimizeToDetect = false);
        CopyTopbarInto(ui.Data.AssetsDir);
        ui.Show();

        // No capture is queued, so this one fails after asking.
        await ui.ViewModel.DetectCommand.Execute();

        Assert.Equal(1, ui.Capture.Captures);
        Assert.False(ui.Capture.Minimized);
    }

    private static SettingRow RowTitled(UiHarness ui, string title) =>
        ui.Window.SettingsPage.GetVisualDescendants().OfType<SettingRow>().Single(row => row.Title == title);

    private static List<string> SegmentsOf(UiHarness ui, string title) =>
        RowTitled(ui, title).GetVisualDescendants().OfType<ListBoxItem>()
            .Select(item => ((UpdateModeOption)item.DataContext!).Label).ToList();

    private static ListBoxItem SegmentOf(UiHarness ui, string title, string label) =>
        RowTitled(ui, title).GetVisualDescendants().OfType<ListBoxItem>().Single(item => ((UpdateModeOption)item.DataContext!).Label == label);

    private static List<object?> TabNames(UiHarness ui) =>
        ui.Window.PageTabs.GetVisualDescendants().OfType<ListBoxItem>().Select(item => item.Content).ToList();

    private static ListBoxItem TabItem(UiHarness ui, string name) =>
        ui.Window.PageTabs.GetVisualDescendants().OfType<ListBoxItem>().Single(item => Equals(item.Content, name));

    private static void Click(TopLevel root, Visual target)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root)!.Value;
        root.MouseDown(at, MouseButton.Left);
        root.MouseUp(at, MouseButton.Left);
        UiHarness.Settle();
    }
}
