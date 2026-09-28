using System.Reactive.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Settings;
using DeadlockAdvisor.Features.Settings.Data;
using DeadlockAdvisor.Features.Settings.Detection;
using DeadlockAdvisor.Features.Settings.General;
using DeadlockAdvisor.Models;
using static DeadlockAdvisor.Tests.Support.VisionData;

namespace DeadlockAdvisor.Tests.Ui;

public class SettingsPageTests
{
    [AvaloniaFact]
    public void TheGearOpensSettingsOverThePagesAndATabClosesIt()
    {
        using var ui = new UiHarness(settings => settings.Current.LastPage = 1);
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
        Assert.Equal(1, ui.Window.PageTabs.SelectedIndex);
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
            settings.Current.LastPage = 2;
            settings.Current.LastMatch = match.ToSaved();
            settings.Current.ReopenLastPage = false;
            settings.Current.ReopenLastMatch = false;
        });

        Assert.True(ui.ViewModel.IsMatchPage);
        Assert.Empty(ui.ViewModel.Match.Match.RoleMap);
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
