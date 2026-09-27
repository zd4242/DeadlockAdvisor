using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match;
using DeadlockAdvisor.Features.Match.Board;
using DeadlockAdvisor.Features.Match.Results;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DeadlockAdvisor.Tests.Ui;

public class MatchPageTests
{
    /// <summary>The line-up the Python app's screenshots use (scratchpad/shoot_pages.py there).</summary>
    private static void SetUpMatch(UiHarness ui)
    {
        var board = ui.ViewModel.Match.Board;
        foreach (var hero in new[] { "haze", "infernus", "vindicta", "abrams", "lash", "seven" })
            board.SetRole(hero, Role.Enemy);
        foreach (var hero in new[] { "dynamo", "kelvin", "paradox", "shiv", "yamato" })
            board.SetRole(hero, Role.Ally);
        board.SetRole("wraith", Role.Self);
        board.ToggleLane("kelvin");
        board.ToggleLane("haze");
        board.ToggleLane("infernus");

        var souls = new Dictionary<string, int>
        {
            ["haze"] = 25_000, ["infernus"] = 19_000, ["vindicta"] = 21_000, ["abrams"] = 17_000, ["lash"] = 22_000, ["seven"] = 16_000,
            ["dynamo"] = 15_000, ["kelvin"] = 14_000, ["paradox"] = 18_000, ["shiv"] = 16_000, ["yamato"] = 20_000, ["wraith"] = 19_000,
        };
        var at = new DateTimeOffset(2026, 9, 26, 20, 0, 0, TimeSpan.Zero);
        ui.ViewModel.Match.Match.NetWorth.Add(new NetWorthSnapshot(at, souls.ToDictionary(entry => entry.Key, entry => entry.Value - 3_000)));
        ui.ViewModel.Match.Match.NetWorth.Add(new NetWorthSnapshot(at.AddMinutes(2), souls));
        board.Refresh();
        ui.ViewModel.Match.Refresh();
    }

    [AvaloniaTheory]
    [InlineData(2, "match_100.png")]
    [InlineData(5, "match_150.png")]
    public void MatchPageRendersAtEachZoom(int zoomIndex, string file)
    {
        using var ui = new UiHarness(settings => settings.Current.ZoomIndex = zoomIndex);
        SetUpMatch(ui);
        ui.ViewModel.Match.ResultsTab = 1;
        ui.Show();

        var full = ui.ViewModel.Match.FullResults;
        var first = full.Entries.OfType<ResultRowViewModel>().First();
        full.Select(first);

        Assert.True(File.Exists(ui.Screenshot(file)));
        Assert.True(ui.ViewModel.Match.Explain.HasItem);
        Assert.NotEmpty(ui.ViewModel.Match.Explain.Contributions);
    }

    [AvaloniaFact]
    public void TieredLaneViewAndEmptyStateRender()
    {
        using var ui = new UiHarness(settings => settings.Current.ResultsByTier = true);
        ui.Show();
        ui.Screenshot("match_empty.png");
        Assert.True(ui.ViewModel.Match.LaneResults.IsEmpty);

        SetUpMatch(ui);
        ui.Screenshot("match_lane_tiered.png");
        Assert.Contains(ui.ViewModel.Match.LaneResults.Entries, entry => entry is SectionHeaderViewModel);
    }

    [AvaloniaFact]
    public void TheFormulaListEndsWithTheDataPicksAndTheIdleExplainPointsToIt()
    {
        using var ui = new UiHarness();
        SetUpMatch(ui);
        ui.ViewModel.Match.ResultsTab = 1;
        ui.Show();
        var full = ui.ViewModel.Match.FullResults;
        var picks = full.Entries.OfType<SectionHeaderViewModel>().Single(header => header.Key == ResultsViewModel.DataPicksKey);
        full.ToggleSection(picks.Key);
        full.ToggleSection(picks.Key);

        // Scrolled to the end, where the data picks sit under the formula's list.
        ScrollResultsToEnd(ui);
        ui.Screenshot("match_data_picks.png");

        Assert.False(ui.ViewModel.Match.Explain.HasItem);
        var pick = full.Entries.OfType<ResultRowViewModel>().Last();
        full.Select(pick);
        Assert.True(ui.ViewModel.Match.Explain.HasItem);
        ui.Screenshot("match_data_pick_explained.png");
    }

    [AvaloniaFact]
    public void RankingByMatchDataWithEveryItemRenders()
    {
        using var ui = new UiHarness();
        SetUpMatch(ui);
        var match = ui.ViewModel.Match;
        match.ResultsTab = 1;
        match.SelectedRank = MatchViewModel.RankPresets.Single(preset => preset.RankBy == RankBy.MatchData);
        match.SelectedCutoff = MatchViewModel.CutoffPresets.Single(preset => preset.MinFraction is null);
        ui.Show();
        ui.Screenshot("match_rank_data_every.png");

        match.SelectedRank = MatchViewModel.RankPresets.Single(preset => preset.RankBy == RankBy.Both);
        match.SelectedCutoff = MatchViewModel.CutoffPresets.Single(preset => preset.Percent == 0);
        match.FullResults.Select(match.FullResults.Entries.OfType<ResultRowViewModel>().First());
        ui.Screenshot("match_rank_blend.png");
        Assert.All(match.FullResults.Entries.OfType<ResultRowViewModel>(), row => Assert.True(row.HasDataBar));
        Assert.StartsWith("Formula", match.Explain.Verdict);
        match.SelectedCutoff = MatchViewModel.CutoffPresets.Single(preset => preset.MinFraction is null);

        match.SelectedRank = MatchViewModel.RankPresets.Single(preset => preset.RankBy == RankBy.Formula);
        ScrollResultsToEnd(ui);
        ui.Screenshot("match_every_item_negatives.png");

        Assert.Contains(match.FullResults.Entries.OfType<ResultRowViewModel>(), row => row.IsNegative);
        Assert.Equal(RankBy.Formula, ui.Settings.Current.ResultsRankBy);
    }

    private static void ScrollResultsToEnd(UiHarness ui)
    {
        UiHarness.Settle();
        ui.Window.MatchPage.GetVisualDescendants().OfType<ResultsView>().Single(view => view.IsEffectivelyVisible)
            .GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
    }

    [AvaloniaFact]
    public void TheHeroPickerRendersOpen()
    {
        using var ui = new UiHarness();
        SetUpMatch(ui);
        ui.Show();
        ui.ViewModel.Match.FocusSearch();
        ui.Screenshot("match_picker.png");
        Assert.True(Picker(ui).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void TypingAHeroAndPressingEnterAssignsIt()
    {
        using var ui = new UiHarness();
        ui.Show();
        var match = ui.ViewModel.Match;
        Assert.False(Picker(ui).IsEffectivelyVisible);

        match.FocusSearch();
        UiHarness.Settle();
        Assert.True(Picker(ui).IsEffectivelyVisible);
        ui.Window.KeyTextInput("haz");
        UiHarness.Settle();
        Assert.Equal("haz", match.Board.SearchText);
        Assert.True(match.Board.Tiles.Single(t => t.HeroId == "haze").IsHighlighted);
        ui.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        UiHarness.Settle();

        Assert.Equal(Role.Enemy, match.Match.RoleOf("haze"));
        Assert.Equal("", match.Board.SearchText);
        Assert.Equal(["haze"], ui.Settings.Current.LastMatch!.Roles.Keys);
    }

    [AvaloniaFact]
    public void EscapeClearsTheSearchAndThenClosesThePicker()
    {
        using var ui = new UiHarness();
        ui.Show();
        var board = ui.ViewModel.Match.Board;

        ui.Window.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.Alt);
        UiHarness.Settle();
        Assert.True(Picker(ui).IsEffectivelyVisible);
        Assert.Equal(Role.Ally, board.Mode);
        ui.Window.KeyTextInput("haz");
        UiHarness.Settle();

        ui.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.Equal("", board.SearchText);
        Assert.True(board.IsPickerOpen);

        ui.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.False(Picker(ui).IsEffectivelyVisible);

        // Typing no longer reaches the hidden search box.
        ui.Window.KeyTextInput("haz");
        ui.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.Equal("", board.SearchText);
        Assert.Empty(ui.ViewModel.Match.Match.OwnTeam);
    }

    private static MatchBoardView Picker(UiHarness ui) =>
        ui.Window.MatchPage.GetVisualDescendants().OfType<MatchBoardView>().Single();

    [AvaloniaFact]
    public async Task HoveringAnItemIconShowsItsCardAfterTheDelay()
    {
        using var ui = new UiHarness();
        SetUpMatch(ui);
        ui.ViewModel.Match.ResultsTab = 1;
        ui.Show();

        var icon = ui.Window.GetVisualDescendants().OfType<Controls.Art.ArtImage>()
            .First(image => Features.Shared.ItemCard.ItemCardHover.GetItemId(image) is not null && image.IsEffectivelyVisible);
        var itemId = Features.Shared.ItemCard.ItemCardHover.GetItemId(icon);
        var center = icon.TranslatePoint(new Point(17, 17), ui.Window)!.Value;

        ui.Window.MouseMove(center);
        UiHarness.Settle();
        Assert.Null(ui.Window.ItemCards.ShownItemId);

        Assert.True(await UiHarness.WaitUntilAsync(() => ui.Window.ItemCards.ShownItemId is not null));
        Assert.Equal(itemId, ui.Window.ItemCards.ShownItemId);

        ui.Window.MouseDown(center, MouseButton.Left);
        UiHarness.Settle();
        Assert.Null(ui.Window.ItemCards.ShownItemId);
    }

    [AvaloniaFact]
    public void CtrlTabCyclesPagesAndZoomKeysStep()
    {
        using var ui = new UiHarness();
        ui.Show();

        ui.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
        Assert.Equal(1, ui.ViewModel.CurrentPage);
        ui.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal(0, ui.ViewModel.CurrentPage);

        ui.Window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.Control);
        Assert.Equal(1.15, ui.ViewModel.UiScale);
        ui.Window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.Control);
        Assert.Equal(1.0, ui.ViewModel.UiScale);
    }

    [AvaloniaFact]
    public async Task TheDataButtonFiltersTheMatchDataByRank()
    {
        using var ui = new UiHarness();
        ui.Show();
        var button = ui.Window.GetVisualDescendants().OfType<DropDownButton>().Single();
        Assert.False(button.IsEnabled); // the golden data predates rank splits
        Assert.Equal("Data: every match", button.Content);

        ui.Services.GetRequiredService<IMatchStatsService>().Apply(ui.Data.Store, await MatchStatsServiceTests.ReplayedCountsAsync());
        ui.Data.NotifyReplaced();
        UiHarness.Settle();
        Assert.True(button.IsEnabled);

        button.Flyout!.ShowAt(button);
        UiHarness.Settle();
        var panel = Assert.IsType<StackPanel>(((Flyout)button.Flyout).Content);
        var radios = panel.Children.OfType<RadioButton>().ToList();
        var combos = panel.Children.OfType<Grid>().Single().Children.OfType<ComboBox>().ToList();
        Assert.True(radios[0].IsChecked);
        Assert.Equal("Initiate", combos[0].SelectedItem?.ToString());

        radios[1].IsChecked = true;
        combos[0].SelectedIndex = 4;
        UiHarness.Settle();

        Assert.False(radios[0].IsChecked);
        Assert.Equal("Data: Mystic+", button.Content);
        Assert.Equal(new RankRange(5, 10), MatchStatsMath.RankOf(ui.Data.Store.MatchMeta));
        Assert.True(File.Exists(ui.Screenshot("match_data_ranks.png")));
    }
}
