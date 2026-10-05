using System.Reactive.Linq;
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
