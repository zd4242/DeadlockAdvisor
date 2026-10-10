using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Controls.Grids;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Settings;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests.Ui;

public class AccessibilityTests
{
    private static AutomationPeer PeerOf(Control control) => ControlAutomationPeer.CreatePeerForElement(control);

    private static string? NameOf(Control control) => PeerOf(control).GetName();

    /// <summary>
    /// The buttons on screen that the app declares: not the ones inside a control's template (a spinner's
    /// arrows, a scroll bar's), which the control names itself.
    /// </summary>
    private static IEnumerable<Button> OwnButtons(Visual root) =>
        root.GetVisualDescendants().OfType<Button>().Where(button => button.TemplatedParent is null && button.IsEffectivelyVisible);

    private static string Describe(Button button) =>
        $"{(string.IsNullOrEmpty(button.Name) ? "" : "#" + button.Name + " ")}[{string.Join(' ', button.Classes)}] "
        + $"tip: {Avalonia.Controls.ToolTip.GetTip(button)}";

    private static bool Reads(string? text) => text is not null && text.Any(char.IsLetterOrDigit);

    /// <summary>
    /// A name set for the screen reader, or words to read out. A button holding only a picture is named
    /// by its type, and one holding only a symbol such as × or + by the symbol: neither is a name.
    /// </summary>
    private static bool IsNamed(Button button)
    {
        if (Reads(AutomationProperties.GetName(button)))
            return true;
        return button.Content switch
        {
            string text => Reads(text),
            Visual visual => visual.GetSelfAndVisualDescendants().OfType<TextBlock>().Any(block => Reads(block.Text)),
            _ => false,
        };
    }

    private static List<string> Unnamed(Visual root) =>
        OwnButtons(root).Where(button => !IsNamed(button)).Select(Describe).ToList();

    [AvaloniaFact]
    public void EveryButtonInTheWindowHasAName()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings));
        MatchPageTests.SetUpMatch(ui);
        ui.ViewModel.Match.Board.IsPickerOpen = true;
        var formulas = ui.ViewModel.ItemFormulas;
        formulas.ByItem.SelectedRow = formulas.ByItem.Items.First(row => row.ItemId == "focus_lens");
        formulas.ByTrait.SelectedCategory = formulas.ByTrait.Categories.First(category => category.CategoryId == "deals_spirit_damage_general");
        ui.Show();
        var missing = new List<string>();
        var seen = new HashSet<string?>();

        void Look()
        {
            UiHarness.Settle();
            missing.AddRange(Unnamed(ui.Window));
            seen.UnionWith(OwnButtons(ui.Window).Select(button => AutomationProperties.GetName(button)));
            missing.AddRange(ui.Window.GetVisualDescendants().OfType<ScrollingGrid>()
                .Where(grid => grid.IsEffectivelyVisible && !Reads(AutomationProperties.GetName(grid)))
                .Select(grid => $"{grid.GetType().Name} has no name"));
        }

        foreach (var tab in Enumerable.Range(0, ui.ViewModel.PageNames.Count))
        {
            ui.ViewModel.SelectedTab = tab;
            Look();
        }
        foreach (var formulaTab in new[] { 0, 1 })
        {
            formulas.SelectedTab = formulaTab;
            Look();
        }
        // The walk reaches the buttons that only a filled-in page has.
        Assert.Contains("Delete this rule", seen);
        Assert.Contains("Delete this stat rule", seen);

        ui.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        foreach (var category in ui.ViewModel.Settings.Categories)
        {
            ui.ViewModel.Settings.SelectedCategory = category;
            UiHarness.Settle();
            missing.AddRange(Unnamed(ui.Window.SettingsPage));
        }

        var chip = ui.Window.StatusBar.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "UpdatesChip");
        chip.Flyout!.ShowAt(chip);
        UiHarness.Settle();
        missing.AddRange(Unnamed(ui.Window));

        Assert.True(missing.Count == 0, "Controls with no name for a screen reader:\n" + string.Join("\n", missing.Distinct()));
    }

    [AvaloniaFact]
    public void ASettingsSwitchIsNamedByItsRowAndTakesItsDescriptionAsHelp()
    {
        using var ui = new UiHarness();
        ui.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        ui.Show();

        var rows = ui.Window.SettingsPage.GetVisualDescendants().OfType<SettingRow>().Where(row => row.Content is ToggleSwitch).ToList();

        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var toggle = (ToggleSwitch)row.Content!;
            Assert.Equal(row.Title, NameOf(toggle));
            Assert.Equal(row.Description, PeerOf(toggle).GetHelpText());
        }
    }

    [AvaloniaFact]
    public void AControlsOwnNameBeatsTheRowsTitle()
    {
        var toggle = new ToggleSwitch();
        AutomationProperties.SetName(toggle, "Its own");

        var row = new SettingRow { Title = "Title", Content = toggle };

        Assert.Equal("Its own", NameOf(toggle));
        Assert.NotNull(row);
    }

    [AvaloniaFact]
    public void PaintedControlsSayWhatTheyAre()
    {
        var slot = new RosterSlot { HeroId = "haze", HeroName = "Haze", Team = Role.Enemy, NetWorth = 25_000, IsFocusTarget = true };
        Assert.Equal("Haze, enemy, focused, net worth 25k", NameOf(slot));
        Assert.Equal(AutomationControlType.Button, PeerOf(slot).GetAutomationControlType());
        Assert.Contains("focus", PeerOf(slot).GetHelpText());
        slot.IsSelf = true;
        slot.Team = Role.Ally;
        slot.IsFocusTarget = false;
        Assert.Equal("Haze, you, net worth 25k", NameOf(slot));
        Assert.Equal("Empty ally slot", NameOf(new RosterSlot { Team = Role.Ally }));

        // A hero read off the bar before the teams are known is neither ally nor enemy, and says what a click does.
        var unsided = new RosterSlot { HeroId = "haze", HeroName = "Haze", Team = Role.Enemy, IsUnsided = true };
        Assert.Equal("Haze, team unknown", NameOf(unsided));
        Assert.Contains("click if this is you", PeerOf(unsided).GetHelpText());
        unsided.IsLikelySelf = true;
        Assert.Equal("Haze, team unknown, probably you", NameOf(unsided));
        Assert.Contains("Click to confirm", PeerOf(unsided).GetHelpText());

        Assert.Equal("Haze, enemy", NameOf(new HeroTile { HeroName = "Haze", Role = Role.Enemy }));
        Assert.Equal("Haze", NameOf(new HeroTile { HeroName = "Haze" }));
        Assert.Equal("T3", NameOf(new Badge { Text = "T3" }));
        Assert.Equal("up 12", NameOf(new SignedAmount { Value = 12, Text = "12" }));
        Assert.Equal("down 3", NameOf(new SignedAmount { Value = -3 }));
        Assert.Equal("0", NameOf(new SignedAmount { Value = 0 }));

        var info = new InfoBadge();
        Avalonia.Controls.ToolTip.SetTip(info, "What this means");
        Assert.Equal("More information", NameOf(info));
        Assert.Equal("What this means", PeerOf(info).GetHelpText());
    }

    [AvaloniaFact]
    public void ADecorativePictureIsLeftOutOfWhatIsRead()
    {
        Assert.False(PeerOf(new ScoreBar { Fraction = 0.5 }).IsContentElement());
        Assert.False(PeerOf(new Controls.Art.ArtImage { ArtId = "haze" }).IsControlElement());
    }

    [AvaloniaFact]
    public void TheMatchBarsSlotsAreNamedOnTheRealPage()
    {
        using var ui = new UiHarness();
        MatchPageTests.SetUpMatch(ui);
        ui.Show();

        var names = ui.Window.MatchPage.GetVisualDescendants().OfType<RosterSlot>().Select(NameOf).ToList();

        Assert.Contains("Haze, enemy, net worth 25k", names);
        Assert.Contains("Wraith, you, net worth 19k", names);
        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
    }
}
