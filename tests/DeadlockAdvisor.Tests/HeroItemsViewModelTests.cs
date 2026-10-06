using System.Reactive.Linq;
using Avalonia.Media;
using DeadlockAdvisor.Features.HeroItems;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

/// <summary>The Hero Items page over a synthetic download.</summary>
public sealed class HeroItemsViewModelTests : IDisposable
{
    private readonly DataFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private HeroItemsViewModel Page() => new(_fixture.Data, _fixture.Settings);

    private async Task DownloadAsync(bool includeRanks = true)
    {
        await SyntheticItemStatsApi.DownloadAsync(_fixture.Data.Store, includeRanks);
        _fixture.Data.NotifyReplaced();
    }

    [Fact]
    public async Task WithoutMatchDataItSaysWhereToGetItAndFillsInOnceItArrives()
    {
        using var page = Page();

        Assert.True(page.IsEmpty);
        Assert.False(page.HasMatchData);
        Assert.Contains("Data → Download Match Data", page.EmptyHint);
        Assert.Empty(page.Rows);

        await DownloadAsync();

        Assert.False(page.IsEmpty);
        Assert.True(page.HasMatchData);
        Assert.Equal(["Patch 09-29", "Patch 09-16"], page.Patches.Select(patch => patch.Label));
        Assert.Equal(MatchMode.Ranked, page.SelectedMode.Mode);
        Assert.NotEmpty(page.Rows);
        Assert.Contains(" ranked matches · ", page.Summary);
        // Most used first, and every one with a change since 09-16.
        Assert.Equal(page.Rows.Select(row => row.Usage).OrderByDescending(usage => usage), page.Rows.Select(row => row.Usage));
        Assert.All(page.Rows, row => Assert.NotEqual("", row.UsageChangeText));
    }

    [Fact]
    public async Task TheUsageSliderHidesRarelyBoughtItemsAndIsRemembered()
    {
        await DownloadAsync();
        using var page = Page();
        var all = page.Rows.Count;
        Assert.Equal(5, page.MinUsagePercent);
        Assert.Equal("", page.HiddenText);

        page.MinUsagePercent = 50;

        Assert.InRange(page.Rows.Count, 1, all - 1);
        Assert.All(page.Rows, row => Assert.True(row.Usage >= 0.5));
        Assert.Equal($"{all - page.Rows.Count} items bought in under 50% of matches are hidden.", page.HiddenText);
        Assert.Equal(50, _fixture.Settings.Current.HeroItemsMinUsagePercent);
        using var reopened = Page();
        Assert.Equal(page.Rows.Count, reopened.Rows.Count);
    }

    [Fact]
    public void AWinRateIsMarkedAboveOrBelowTheHerosAverageOnlyBeyondHalfAPoint()
    {
        var item = new DeadlockAdvisor.Models.Item("boots", "Boots", "vitality", 1, GameId: 1, Cost: 800);
        HeroItemRowViewModel Row(long wins) => new(new HeroItemRow(item, wins, 1000, 0.5, null, null), average: 0.5);

        Assert.Equal((true, false), (Row(560).AboveAverage, Row(560).BelowAverage));
        Assert.Equal((false, true), (Row(440).AboveAverage, Row(440).BelowAverage));
        Assert.Equal((false, false), (Row(504).AboveAverage, Row(504).BelowAverage));
        Assert.Equal((true, false), (Row(505).AboveAverage, Row(505).BelowAverage));
        Assert.Equal("+6.0 points on the hero's 50.00% average win rate with these filters", Row(560).WinRateTip);
    }

    [Fact]
    public void ATintBehindTheWinRateGrowsWithItsDistanceFromTheAverageAndIsClearForAnAverageOne()
    {
        var item = new DeadlockAdvisor.Models.Item("boots", "Boots", "vitality", 1, GameId: 1, Cost: 800);
        byte AlphaOf(long wins) => ((ISolidColorBrush)new HeroItemRowViewModel(new HeroItemRow(item, wins, 1000, 0.5, null, null), average: 0.5).WinRateFill).Color.A;

        Assert.Equal(0, AlphaOf(504));
        Assert.InRange(AlphaOf(510), 1, AlphaOf(530) - 1);
        Assert.InRange(AlphaOf(530), AlphaOf(510) + 1, AlphaOf(550) - 1);
        Assert.Equal(AlphaOf(550), AlphaOf(700));
        var above = (ISolidColorBrush)new HeroItemRowViewModel(new HeroItemRow(item, 560, 1000, 0.5, null, null), 0.5).WinRateFill;
        var below = (ISolidColorBrush)new HeroItemRowViewModel(new HeroItemRow(item, 440, 1000, 0.5, null, null), 0.5).WinRateFill;
        Assert.Equal((DeadlockAdvisor.Theme.Palette.Positive.G, DeadlockAdvisor.Theme.Palette.Negative.G), (above.Color.G, below.Color.G));
    }

    [Fact]
    public async Task SeveralPatchesAddUpAndTheLastOneTickedStays()
    {
        await DownloadAsync();
        using var page = Page();
        Assert.Equal("Patch 09-29", page.PatchSummary);
        Assert.Equal([true, false], page.Patches.Select(patch => patch.IsChecked));
        var single = page.Summary;
        Assert.DoesNotContain(" patches · ", single);

        page.Patches[1].IsChecked = true;

        Assert.Equal("Patches 09-16 – 09-29", page.PatchSummary);
        Assert.Contains(" over 2 patches · ", page.Summary);
        Assert.Contains("before the oldest", page.ChangeTip);
        // Nothing comes before 09-16, so there's no change to show.
        Assert.All(page.Rows, row => Assert.Equal("", row.UsageChangeText));

        page.Patches[0].IsChecked = false;
        Assert.Equal("Patch 09-16", page.PatchSummary);
        Assert.DoesNotContain(" patches · ", page.Summary);

        page.Patches[1].IsChecked = false;
        Assert.Equal([false, true], page.Patches.Select(patch => patch.IsChecked));

        await page.PickLatestPatchCommand.Execute();
        Assert.Equal(("Patch 09-29", single), (page.PatchSummary, page.Summary));
        await page.PickAllPatchesCommand.Execute();
        Assert.Equal("Patches 09-16 – 09-29", page.PatchSummary);
    }

    [Fact]
    public async Task ThePatchesPickedSurviveANewDownload()
    {
        await DownloadAsync();
        using var page = Page();
        await page.PickAllPatchesCommand.Execute();

        await DownloadAsync();

        Assert.Equal([true, true], page.Patches.Select(patch => patch.IsChecked));
    }

    [Fact]
    public async Task TheFormulaCanBeAskedForOnlyWhileTheEditorsAreShown()
    {
        await DownloadAsync();
        using var page = Page();
        var asked = new List<string>();
        using var _ = page.FormulaRequested.Subscribe(asked.Add);
        var command = (System.Windows.Input.ICommand)page.OpenFormulaCommand;
        Assert.False(page.ShowsEditors);
        Assert.False(command.CanExecute("sprint_boots"));

        _fixture.Settings.Update(settings => settings.ShowModelEditors = true);

        Assert.True(page.ShowsEditors);
        await page.OpenFormulaCommand.Execute("sprint_boots");
        Assert.Equal(["sprint_boots"], asked);
    }

    [Fact]
    public async Task TierPillsLeaveOutTheirTiers()
    {
        await DownloadAsync();
        using var page = Page();
        var store = _fixture.Data.Store;
        int TierOf(HeroItemRowViewModel row) => store.Items[row.ItemId].Tier;
        Assert.Contains(page.Rows, row => TierOf(row) == 1);

        page.Tiers[0].IsChecked = false;

        Assert.NotEmpty(page.Rows);
        Assert.DoesNotContain(page.Rows, row => TierOf(row) == 1);
    }

    [Fact]
    public async Task AHeaderSortsByItsColumnAndAgainTheOtherWay()
    {
        await DownloadAsync();
        using var page = Page();
        Assert.Equal("Usage ▾", page.UsageHeader.Text);

        await page.SortCommand.Execute(HeroItemSort.Item);
        var names = page.Rows.Select(row => row.Name).ToList();
        Assert.Equal(names.Order(StringComparer.OrdinalIgnoreCase), names);
        Assert.Equal(("Item ▴", "Usage"), (page.ItemHeader.Text, page.UsageHeader.Text));

        await page.SortCommand.Execute(HeroItemSort.Item);
        Assert.Equal(names.AsEnumerable().Reverse(), page.Rows.Select(row => row.Name));
        Assert.Equal("Item ▾", page.ItemHeader.Text);
    }

    [Fact]
    public async Task ARankRangeNarrowsTheMatchesAndUnrankedHasNoneToPick()
    {
        await DownloadAsync();
        using var page = Page();
        page.SelectedMode = HeroItemsViewModel.Modes.Single(mode => mode.Mode == MatchMode.All);
        var every = page.Summary;
        Assert.True(page.CanPickRanks);

        page.From = page.Ranks[^1];

        Assert.NotEqual(every, page.Summary);
        Assert.Equal(page.Ranks[^1], page.To);

        page.SelectedMode = HeroItemsViewModel.Modes.Single(mode => mode.Mode == MatchMode.Unranked);
        Assert.False(page.CanPickRanks);
        Assert.Contains(" unranked matches · ", page.Summary);
        Assert.Equal(MatchMode.Unranked, _fixture.Settings.Current.HeroItemsMode);
    }

    [Fact]
    public async Task WithoutRankGroupsTheRanksCantBePicked()
    {
        await DownloadAsync(includeRanks: false);
        using var page = Page();

        Assert.Empty(page.Ranks);
        Assert.False(page.CanPickRanks);
        Assert.False(page.IsEmpty);
    }

    [Fact]
    public async Task ItOpensOnYourHeroOnceAndThenKeepsTheHeroYouPick()
    {
        await DownloadAsync();
        using var page = Page();
        var heroes = page.Heroes;

        page.ShowSelf(heroes[3].HeroId);
        Assert.Equal(heroes[3], page.SelectedHero);

        page.SelectedHero = heroes[5];
        page.ShowSelf(heroes[3].HeroId);
        Assert.Equal(heroes[5], page.SelectedHero);
        Assert.Equal(heroes[5].HeroId, _fixture.Settings.Current.HeroItemsHero);

        page.ShowSelf(heroes[4].HeroId);
        Assert.Equal(heroes[4], page.SelectedHero);
    }
}
