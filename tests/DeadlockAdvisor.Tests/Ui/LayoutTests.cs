using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Tests.Fakes;

namespace DeadlockAdvisor.Tests.Ui;

/// <summary>
/// Every page at the smallest window the app allows. The pages are laid out for 900 × 600 at 100%, so
/// at 150% the window's minimum grows to 1350 × 900 and shows the same room: both render the same page.
/// </summary>
public class LayoutTests
{
    /// <summary>
    /// What a page paints beyond the edge of the area that holds it (the window, or the scroll viewer
    /// around it): cut off, since a scroll viewer that only scrolls down has no way to the rest. A
    /// viewer that scrolls sideways is allowed to hold wider things.
    /// </summary>
    private static List<string> Overflowing(Visual page)
    {
        var window = (TopLevel)page.GetVisualRoot()!;
        double RightOf(Control control) => control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value.X;

        var found = new List<string>();
        foreach (var control in page.GetVisualDescendants().OfType<Control>())
        {
            if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control is ScrollContentPresenter)
                continue;
            var ancestors = control.GetVisualAncestors().ToList();
            if (ancestors.OfType<ScrollViewer>().Any(viewer => viewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled))
                continue;

            var limit = ancestors.OfType<ScrollContentPresenter>().FirstOrDefault() is { } holder ? RightOf(holder) : window.ClientSize.Width;
            if (RightOf(control) > limit + 1)
                found.Add($"{control.GetType().Name}{(string.IsNullOrEmpty(control.Name) ? "" : " #" + control.Name)} ends at {RightOf(control):0} past {limit:0}");
        }
        return found;
    }

    [AvaloniaTheory]
    [InlineData(2, "100")]
    [InlineData(5, "150")]
    public async Task EveryPageFitsTheSmallestWindow(int zoomIndex, string percent)
    {
        using var ui = new UiHarness(settings =>
        {
            UiHarness.Editing(settings);
            settings.Current.ZoomIndex = zoomIndex;
            settings.Current.HeroItemsShowChanges = true;
        });
        await SyntheticItemStatsApi.DownloadAsync(ui.Data.Store);
        ui.Data.NotifyReplaced();
        MatchPageTests.SetUpMatch(ui);
        ui.Show();
        ui.ViewModel.Match.Results.Select(ui.ViewModel.Match.Results.Entries.OfType<Features.Match.Results.ResultRowViewModel>().First());
        var scale = ZoomLevels.Steps[zoomIndex];

        Smallest(ui);
        Assert.Equal(900 * scale, ui.Window.ClientSize.Width, 1);
        Assert.Equal(600 * scale, ui.Window.ClientSize.Height, 1);

        var problems = new List<string>();
        void Look(string name)
        {
            UiHarness.Settle();
            ui.Screenshot($"layout_{name}_{percent}.png");
            problems.AddRange(Overflowing(ui.Window).Select(line => $"{name}: {line}"));
        }

        var formulas = ui.ViewModel.ItemFormulas;
        formulas.ByItem.SelectedRow = formulas.ByItem.Items.First(row => row.ItemId == "focus_lens");
        formulas.ByTrait.SelectedCategory = formulas.ByTrait.Categories.First(category => category.CategoryId == "deals_spirit_damage_general");
        foreach (var (tab, name) in new[] { (0, "match"), (1, "hero_items"), (2, "hero_traits"), (3, "by_item") })
        {
            ui.ViewModel.SelectedTab = tab;
            Look(name);
        }
        formulas.SelectedTab = 1;
        Look("by_trait");

        ui.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        foreach (var category in ui.ViewModel.Settings.Categories)
        {
            ui.ViewModel.Settings.SelectedCategory = category;
            Look("settings_" + category.Page.GetType().Name.Replace("SettingsViewModel", "").ToLowerInvariant());
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
    }

    /// <summary>As small as the user can drag it. Asking for less than the minimum would leave the headless surface empty.</summary>
    private static void Smallest(UiHarness ui) => Resize(ui, ui.Window.MinWidth, ui.Window.MinHeight);

    private static void Resize(UiHarness ui, double width, double height)
    {
        ui.Window.Width = width;
        ui.Window.Height = height;
        UiHarness.Settle();
    }

    [AvaloniaFact]
    public void TheSmallestWindowFollowsTheZoomUpToWhatTheScreenHas()
    {
        foreach (var (index, scale) in ZoomLevels.Steps.Select((scale, index) => (index, scale)))
        {
            using var ui = new UiHarness(settings => settings.Current.ZoomIndex = index);
            ui.Show();
            var room = ui.Window.Screens.ScreenFromWindow(ui.Window) is { } screen
                ? new Size(screen.WorkingArea.Width / screen.Scaling, screen.WorkingArea.Height / screen.Scaling)
                : new Size(double.PositiveInfinity, double.PositiveInfinity);

            Assert.Equal(Math.Min(900 * scale, Math.Max(room.Width, 900)), ui.Window.MinWidth, 3);
            Assert.Equal(Math.Min(600 * scale, Math.Max(room.Height, 600)), ui.Window.MinHeight, 3);
        }
    }

    [AvaloniaFact]
    public void ZoomingInGrowsAWindowThatNoLongerHasTheRoom()
    {
        using var ui = new UiHarness();
        ui.Show();
        Smallest(ui);
        Assert.Equal(900, ui.Window.ClientSize.Width, 1);

        ui.ViewModel.ZoomInCommand.Execute().Subscribe();
        ui.ViewModel.ZoomInCommand.Execute().Subscribe();
        UiHarness.Settle();

        Assert.Equal(900 * ui.ViewModel.UiScale, ui.Window.ClientSize.Width, 1);
    }

    [AvaloniaFact]
    public void TheWindowWaitsForThePointerToLeaveTheZoomButtonsBeforeGrowing()
    {
        using var ui = new UiHarness();
        ui.Show();
        Smallest(ui);
        var zoomIn = ui.Window.StatusBar.GetVisualDescendants().OfType<Button>().First(button => AutomationProperties.GetName(button) == "Zoom in");
        var over = zoomIn.TranslatePoint(new Point(zoomIn.Bounds.Width / 2, zoomIn.Bounds.Height / 2), ui.Window)!.Value;
        ui.Window.MouseMove(over);
        UiHarness.Settle();

        ui.ViewModel.ZoomInCommand.Execute().Subscribe();
        ui.ViewModel.ZoomInCommand.Execute().Subscribe();
        UiHarness.Settle();
        Assert.Equal(900, ui.Window.ClientSize.Width, 1);

        ui.Window.MouseMove(new Point(100, 100));
        UiHarness.Settle();
        Assert.Equal(900 * ui.ViewModel.UiScale, ui.Window.ClientSize.Width, 1);
    }

    [AvaloniaFact]
    public void TheResultsMoveTheirDataBelowTheNameWhenTheListIsNarrow()
    {
        using var ui = new UiHarness();
        MatchPageTests.SetUpMatch(ui);
        ui.Show();
        DataText First() => ui.Window.MatchPage.GetVisualDescendants().OfType<DataText>().First(text => text.IsEffectivelyVisible);

        Resize(ui, 1600, 1000);
        Assert.Equal((0, 5), (Grid.GetRow(First()), Grid.GetColumn(First())));

        Smallest(ui);
        Assert.Equal((1, 0), (Grid.GetRow(First()), Grid.GetColumn(First())));

        Resize(ui, 1600, 1000);
        Assert.Equal((0, 5), (Grid.GetRow(First()), Grid.GetColumn(First())));
    }

    [AvaloniaFact]
    public void TheExplainLinesPutTheirArithmeticBelowTheNameWhenThePanelIsNarrow()
    {
        using var ui = new UiHarness();
        MatchPageTests.SetUpMatch(ui);
        ui.Show();
        var results = ui.ViewModel.Match.Results;
        results.Select(results.Entries.OfType<Features.Match.Results.ResultRowViewModel>().First());
        Grid Line() => ui.Window.MatchPage.GetVisualDescendants().OfType<Grid>().First(grid => grid.Classes.Contains("traitLine"));

        Resize(ui, 1600, 1000);
        Assert.True(Line().ColumnDefinitions[1].ActualWidth > 0);

        Smallest(ui);
        Assert.Equal(0, Line().ColumnDefinitions[1].ActualWidth);
        Assert.True(Line().ColumnDefinitions[0].ActualWidth > 150, "The trait's name has the room the arithmetic left");

        Resize(ui, 1600, 1000);
        Assert.True(Line().ColumnDefinitions[1].ActualWidth > 0);
    }

    [AvaloniaFact]
    public void TheByItemCardPutsItselfAwayWhenTheRulesWouldBeSqueezedAndCanBeAskedBack()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings, 3));
        var page = ui.ViewModel.ItemFormulas.ByItem;
        page.SelectedRow = page.Items.First(row => row.ItemId == "focus_lens");
        ui.Show();

        Resize(ui, 1600, 1000);
        Assert.True(page.ShowCard);
        Assert.True(ui.Window.ItemFormulasPage.ByItemPanel.CardScroller.IsEffectivelyVisible);

        Smallest(ui);
        Assert.False(page.ShowCard);
        Assert.False(ui.Window.ItemFormulasPage.ByItemPanel.CardScroller.IsEffectivelyVisible);

        page.ShowCard = true;
        UiHarness.Settle();
        Assert.True(ui.Window.ItemFormulasPage.ByItemPanel.CardScroller.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task TheHeroItemsNameGivesWayToTheColumnsBesideIt()
    {
        using var ui = new UiHarness(settings =>
        {
            settings.Current.LastPage = 1;
            settings.Current.HeroItemsShowChanges = true;
        });
        await SyntheticItemStatsApi.DownloadAsync(ui.Data.Store);
        ui.Data.NotifyReplaced();
        ui.Show();
        Smallest(ui);

        var rows = ui.Window.HeroItemsPage.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("row")).ToList();

        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var name = row.GetVisualDescendants().OfType<TextBlock>().First();
            var cost = row.GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("cost"));
            Assert.True(name.TranslatePoint(new Point(name.Bounds.Width, 0), ui.Window)!.Value.X
                <= cost.TranslatePoint(default, ui.Window)!.Value.X + 1, $"{name.Text} runs into the next column");
        }
    }
}
