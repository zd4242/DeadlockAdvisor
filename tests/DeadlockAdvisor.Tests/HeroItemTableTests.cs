using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;

namespace DeadlockAdvisor.Tests;

/// <summary>One hero's item table from handmade counts.</summary>
public class HeroItemTableTests
{
    private static readonly Item _boots = new("sprint_boots", "Sprint Boots", "vitality", 1, GameId: 1, Cost: 800);
    private static readonly Item _charge = new("extra_charge", "Extra Charge", "spirit", 1, GameId: 2, Cost: 800);
    private static readonly Item _refresher = new("refresher", "Refresher", "spirit", 4, GameId: 3, Cost: 6400);
    private static readonly Item[] _items = [_boots, _charge, _refresher];

    private static readonly Patch _current = new("09-29-2026", 1790726400);
    private static readonly Patch _previous = new("09-16-2026 Update", 1789603200);
    private static readonly Patch _oldest = new("09-02-2026 Update", 1788480000);
    private static readonly Patch _before = new("08-19-2026 Update", 1787356800);

    private static readonly RankBucket _low = new(1, 5, "Initiate", "Mystic", 0, 59);
    private static readonly RankBucket _high = new(6, 11, "Ritualist", "Eternus", 60, 116);

    private static Dictionary<long, WinTotals> Items(params (long Id, long Wins, long Matches)[] items) =>
        items.ToDictionary(item => item.Id, item => new WinTotals(item.Wins, item.Matches));

    /// <summary>Dynamo's counts over every match (any mode), and in ranked ones alone.</summary>
    private static SliceCounts Slice(WinTotals matches, Dictionary<long, WinTotals> items, HeroCounts? ranked = null) =>
        new(Halves.Empty, new() { ["dynamo"] = new Halves(items, []) }, [])
        {
            HeroMatches = new() { ["dynamo"] = matches },
            Ranked = ranked is null ? [] : new() { ["dynamo"] = ranked },
        };

    private static MatchSegment Segment(Patch patch, SliceCounts everyMatch, params SliceCounts[] byRank) =>
        new(patch, patch.Start, patch.Start + 86400, false, patch.Start + 86400, everyMatch, byRank.Length == 0 ? [] : [_low, _high], byRank);

    [Fact]
    public void RowsHaveTheirWinRateAndUsageMostUsedFirstAndLeaveOutItemsTheStoreDoesntKnow()
    {
        var segment = Segment(_current, Slice(new WinTotals(510, 1000), Items((1, 300, 550), (2, 450, 800), (3, 60, 100), (99, 5, 10))));

        var table = HeroItemTable.Build([segment], [segment], "dynamo", MatchMode.All, null, _items)!;

        Assert.Equal(new WinTotals(510, 1000), table.Matches);
        Assert.Equal(["Extra Charge", "Sprint Boots", "Refresher"], table.Rows.Select(row => row.Item.ItemName));
        var charge = table.Rows[0];
        Assert.Equal((450, 350, 0.5625, 0.8), (charge.Wins, charge.Losses, charge.WinRate, charge.Usage));
        // Nothing to compare with.
        Assert.All(table.Rows, row => Assert.Equal((null, null), (row.WinRateChange, row.UsageChange)));
    }

    [Fact]
    public void ChangesAreSinceThePatchBeforeAndAnItemNewThisPatchHasNoWinRateToCompare()
    {
        var current = Segment(_current, Slice(new WinTotals(500, 1000), Items((1, 300, 500), (3, 60, 100))));
        var previous = Segment(_previous, Slice(new WinTotals(1000, 2000), Items((1, 400, 800))));

        var table = HeroItemTable.Build([current, previous], [current], "dynamo", MatchMode.All, null, _items)!;

        var boots = table.Rows.Single(row => row.Item == _boots);
        Assert.Equal(0.6 - 0.5, boots.WinRateChange!.Value, 9);
        Assert.Equal(0.5 - 0.4, boots.UsageChange!.Value, 9);
        var refresher = table.Rows.Single(row => row.Item == _refresher);
        Assert.Null(refresher.WinRateChange);
        Assert.Equal(0.1, refresher.UsageChange!.Value, 9);

        // The patch before has no patch before it among the ones kept.
        var older = HeroItemTable.Build([current, previous], [previous], "dynamo", MatchMode.All, null, _items)!;
        Assert.Null(older.Rows.Single().UsageChange);
    }

    [Fact]
    public void SeveralPatchesAddUpAndAreComparedWithTheOneBeforeTheOldest()
    {
        var newest = Segment(_current, Slice(new WinTotals(500, 1000), Items((1, 300, 500))));
        var middle = Segment(_previous, Slice(new WinTotals(1000, 2000), Items((1, 400, 800))));
        var oldest = Segment(_oldest, Slice(new WinTotals(250, 500), Items((1, 100, 250))));
        var before = Segment(_before, Slice(new WinTotals(300, 600), Items((1, 120, 300))));

        var table = HeroItemTable.Build([newest, middle, oldest, before], [newest, middle, oldest], "dynamo", MatchMode.All, null, _items)!;

        var boots = table.Rows.Single();
        Assert.Equal((new WinTotals(1750, 3500), 3), (table.Matches, table.PatchCount));
        Assert.Equal((800, 1550), (boots.Wins, boots.Matches));
        Assert.Equal(800.0 / 1550 - 120.0 / 300, boots.WinRateChange!.Value, 9);
        Assert.Equal(1550.0 / 3500 - 300.0 / 600, boots.UsageChange!.Value, 9);
    }

    [Fact]
    public void APatchWithoutTheRankGroupsARangeNeedsIsLeftOutAndCounted()
    {
        var everyMatch = Slice(new WinTotals(500, 1000), Items((1, 300, 500)));
        var ranked = Segment(_current, everyMatch,
            Slice(new WinTotals(80, 150), Items((1, 50, 90))),
            Slice(new WinTotals(50, 100), Items((1, 40, 60))));
        var unranked = Segment(_previous, everyMatch);

        var table = HeroItemTable.Build([ranked, unranked], [ranked, unranked], "dynamo", MatchMode.Ranked, new RankRange(9, 11), _items)!;

        Assert.Equal((100, 1), (table.Matches.Matches, table.PatchCount));
        Assert.Null(HeroItemTable.Build([ranked, unranked], [unranked], "dynamo", MatchMode.Ranked, new RankRange(9, 11), _items));
    }

    [Fact]
    public void RankedIsItsOwnCountsAndUnrankedIsTheRestOfEveryMatch()
    {
        var segment = Segment(_current, Slice(new WinTotals(500, 1000), Items((1, 300, 500)),
            new HeroCounts(new WinTotals(130, 250), Items((1, 90, 150)))));

        var ranked = HeroItemTable.Build([segment], [segment], "dynamo", MatchMode.Ranked, null, _items)!;
        var unranked = HeroItemTable.Build([segment], [segment], "dynamo", MatchMode.Unranked, null, _items)!;

        Assert.Equal((250, 150, 0.6), (ranked.Matches.Matches, ranked.Rows[0].Matches, ranked.Rows[0].Usage));
        Assert.Equal((750, 350, 210), (unranked.Matches.Matches, unranked.Rows[0].Matches, unranked.Rows[0].Wins));
    }

    [Fact]
    public void ARankRangeAddsUpItsGroupsAndNeedsThem()
    {
        var everyMatch = Slice(new WinTotals(500, 1000), Items((1, 300, 500)), new HeroCounts(new WinTotals(130, 250), Items((1, 90, 150))));
        var segment = Segment(_current, everyMatch,
            Slice(new WinTotals(80, 150), Items((1, 50, 90))),
            Slice(new WinTotals(50, 100), Items((1, 40, 60))));

        var high = HeroItemTable.Build([segment], [segment], "dynamo", MatchMode.Ranked, new RankRange(9, 11), _items)!;
        var every = HeroItemTable.Build([segment], [segment], "dynamo", MatchMode.All, new RankRange(1, 11), _items)!;

        Assert.Equal((100, 60), (high.Matches.Matches, high.Rows[0].Matches));
        Assert.Equal((250, 150), (every.Matches.Matches, every.Rows[0].Matches));
        // Unranked matches have no rank, so a range has none of them.
        Assert.Empty(HeroItemTable.Build([segment], [segment], "dynamo", MatchMode.Unranked, new RankRange(1, 11), _items)!.Rows);
        Assert.Null(HeroItemTable.Build([], [Segment(_current, everyMatch)], "dynamo", MatchMode.All, new RankRange(1, 11), _items));
    }

    [Fact]
    public void AHeroWithoutCountsHasAnEmptyTable()
    {
        var segment = Segment(_current, Slice(new WinTotals(500, 1000), Items((1, 300, 500))));

        var table = HeroItemTable.Build([segment], [segment], "haze", MatchMode.All, null, _items)!;

        Assert.Equal(0, table.Matches.Matches);
        Assert.Empty(table.Rows);
    }
}
