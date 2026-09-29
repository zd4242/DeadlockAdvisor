using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using DeadlockAdvisor.Controls;
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
        ui.Show();

        var results = ui.ViewModel.Match.Results;
        results.Select(results.Entries.OfType<ResultRowViewModel>().First());

        Assert.True(File.Exists(ui.Screenshot(file)));
        Assert.True(ui.ViewModel.Match.Explain.HasItem);
        Assert.NotEmpty(ui.ViewModel.Match.Explain.Contributions);
    }

    [AvaloniaFact]
    public void TheTieredViewAndEmptyStateRender()
    {
        using var ui = new UiHarness(settings => settings.Current.ResultsByTier = true);
        ui.Show();
        ui.Screenshot("match_empty.png");
        Assert.True(ui.ViewModel.Match.Results.IsEmpty);

        SetUpMatch(ui);
        ui.Screenshot("match_tiered.png");
        Assert.Contains(ui.ViewModel.Match.Results.Entries, entry => entry is SectionHeaderViewModel);
    }

    [AvaloniaFact]
    public void TheFormulaListEndsWithTheDataPicksAndTheIdleExplainPointsToIt()
    {
        using var ui = new UiHarness(settings => settings.Current.ResultsRankBy = RankBy.Formula);
        SetUpMatch(ui);
        ui.Show();
        var results = ui.ViewModel.Match.Results;
        var picks = results.Entries.OfType<SectionHeaderViewModel>().Single(header => header.Key == ResultsViewModel.DataPicksKey);
        results.ToggleSection(picks.Key);
        results.ToggleSection(picks.Key);

        // Scrolled to the end, where the data picks sit under the formula's list.
        ScrollResultsToEnd(ui);
        ui.Screenshot("match_data_picks.png");

        Assert.False(ui.ViewModel.Match.Explain.HasItem);
        var pick = results.Entries.OfType<ResultRowViewModel>().Last();
        results.Select(pick);
        Assert.True(ui.ViewModel.Match.Explain.HasItem);
        ui.Screenshot("match_data_pick_explained.png");
    }

    [AvaloniaFact]
    public void TheTypicalTeamAndTheMatchDataExplainThemselvesBehindInfoBadges()
    {
        using var ui = new UiHarness();
        SetUpMatch(ui);
        var match = ui.ViewModel.Match;
        match.SelectedCutoff = MatchViewModel.CutoffPresets.Single(preset => preset.MinFraction is null);
        ui.Show();

        match.Results.Select(match.Results.Entries.OfType<ResultRowViewModel>().Single(row => row.ItemId == "knockdown"));
        ui.Screenshot("match_typical_team_info.png");

        var tips = ui.Window.MatchPage.GetVisualDescendants().OfType<InfoBadge>()
            .Where(badge => badge.IsVisible)
            .Select(badge => ToolTip.GetTip(badge) as string)
            .ToList();
        Assert.Equal(2, tips.Count);
        Assert.Contains(tips, tip => tip!.StartsWith("Knockdown is cast on one enemy at a time", StringComparison.Ordinal));
        Assert.Contains(tips, tip => tip!.StartsWith("Second opinion from real matches", StringComparison.Ordinal));
        Assert.StartsWith("Fetched ", match.Explain.MatchData!.Source);
    }

    [AvaloniaFact]
    public void RankingByMatchDataWithEveryItemRenders()
    {
        using var ui = new UiHarness();
        SetUpMatch(ui);
        var match = ui.ViewModel.Match;
        match.SelectedRank = MatchViewModel.RankPresets.Single(preset => preset.RankBy == RankBy.MatchData);
        match.SelectedCutoff = MatchViewModel.CutoffPresets.Single(preset => preset.MinFraction is null);
        ui.Show();
        ui.Screenshot("match_rank_data_every.png");

        match.SelectedRank = MatchViewModel.RankPresets.Single(preset => preset.RankBy == RankBy.Both);
        match.SelectedCutoff = MatchViewModel.CutoffPresets.Single(preset => preset.Percent == 0);
        match.Results.Select(match.Results.Entries.OfType<ResultRowViewModel>().First());
        ui.Screenshot("match_rank_blend.png");
        Assert.All(match.Results.Entries.OfType<ResultRowViewModel>(), row => Assert.True(row.HasDataBar));
        Assert.NotNull(match.Explain.Verdict?.Formula);
        match.SelectedCutoff = MatchViewModel.CutoffPresets.Single(preset => preset.MinFraction is null);

        match.SelectedRank = MatchViewModel.RankPresets.Single(preset => preset.RankBy == RankBy.Formula);
        ScrollResultsToEnd(ui);
        ui.Screenshot("match_every_item_negatives.png");

        Assert.Contains(match.Results.Entries.OfType<ResultRowViewModel>(), row => row.IsNegative);
        Assert.Equal(RankBy.Formula, ui.Settings.Current.ResultsRankBy);
    }

    [AvaloniaFact]
    public void TheRankOptionsKeepTheirTextPlainWhenHovered()
    {
        using var ui = new UiHarness();
        ui.Show();
        var rank = ui.Window.MatchPage.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.ItemsSource == MatchViewModel.RankPresets);
        rank.IsDropDownOpen = true;
        UiHarness.Settle();

        // Gold is the formula's colour, so only the words that name it may be gold.
        var option = TopLevel.GetTopLevel(rank.GetVisualDescendants().OfType<Popup>().Single().Child!)!
            .GetVisualDescendants().OfType<ComboBoxItem>().First();
        Assert.Same(ui.Window.FindResource("TextBrush"), option.FindResource("ComboBoxItemForegroundPointerOver"));
        Assert.Same(ui.Window.FindResource("TextBrush"), option.FindResource("ComboBoxItemForegroundSelected"));
        rank.IsDropDownOpen = false;
    }

    private static void ScrollResultsToEnd(UiHarness ui)
    {
        UiHarness.Settle();
        ui.Window.MatchPage.GetVisualDescendants().OfType<ResultsView>().Single()
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

        Assert.Equal(Role.Self, match.Match.RoleOf("haze"));
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
        using var ui = new UiHarness(settings => UiHarness.Editing(settings));
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
    public async Task TheFiltersMenuFiltersTheMatchDataByRank()
    {
        using var ui = new UiHarness();
        ui.Show();
        var button = ui.Window.MatchPage.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "FiltersButton");
        Assert.Equal(0, ui.ViewModel.Match.ChangedFilters);

        button.Flyout!.ShowAt(button);
        UiHarness.Settle();
        var content = (Control)((Flyout)button.Flyout).Content!;
        var radios = content.GetLogicalDescendants().OfType<RadioButton>().ToList();
        Assert.All(radios, radio => Assert.False(radio.IsEffectivelyEnabled)); // the golden data predates rank splits
        button.Flyout.Hide();

        ui.Services.GetRequiredService<IMatchStatsService>().Apply(ui.Data.Store, await MatchStatsServiceTests.ReplayedCountsAsync());
        ui.Data.NotifyReplaced();
        UiHarness.Settle();

        button.Flyout.ShowAt(button);
        UiHarness.Settle();
        Assert.All(radios, radio => Assert.True(radio.IsEffectivelyEnabled));
        var from = content.GetLogicalDescendants().OfType<ComboBox>().First(combo => combo.DataContext is DataRanksViewModel);
        Assert.True(radios[0].IsChecked);
        Assert.Equal("Initiate", from.SelectedItem?.ToString());

        radios[1].IsChecked = true;
        from.SelectedIndex = 4;
        UiHarness.Settle();

        Assert.False(radios[0].IsChecked);
        Assert.Equal(1, ui.ViewModel.Match.ChangedFilters);
        Assert.Equal(new RankRange(5, 10), MatchStatsMath.RankOf(ui.Data.Store.MatchMeta));
        Assert.True(File.Exists(ui.Screenshot("match_data_ranks.png")));
    }

    [AvaloniaFact]
    public void TheFiltersButtonCountsTheOptionsChangedFromTheirDefaults()
    {
        using var ui = new UiHarness();
        var match = ui.ViewModel.Match;
        Assert.Equal((0, false), (match.ChangedFilters, match.HasChangedFilters));

        match.ByTier = true;
        match.SelectedCutoff = MatchViewModel.CutoffPresets.Single(preset => preset.MinFraction is null);
        Assert.Equal((2, true), (match.ChangedFilters, match.HasChangedFilters));

        // Leaning on net worth only counts once there's a reading to lean on.
        match.ByNetWorth = true;
        Assert.Equal(2, match.ChangedFilters);
        SetUpMatch(ui);
        Assert.Equal(3, match.ChangedFilters);
    }
}
