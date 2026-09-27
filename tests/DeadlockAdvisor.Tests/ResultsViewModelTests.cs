using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match.Results;
using DeadlockAdvisor.Scoring;

namespace DeadlockAdvisor.Tests;

public class ResultsViewModelTests
{
    private static ScoredItem Scored(string itemId, double score, double? against = null, double? mine = null, int tier = 1)
    {
        var data = new OrderedDictionary<string, double>();
        if (against is { } enemies)
            data["against"] = enemies;
        if (mine is { } you)
            data["as"] = you;
        return new ScoredItem(itemId, itemId.ToUpperInvariant(), tier, score, "weapon", data);
    }

    // a and b are the formula's picks; c is a data standout the formula scores below 0; d's strong
    // counter is netted away by how badly it does on you; e has no rules and no data.
    private static readonly IReadOnlyList<ScoredItem> _items =
    [
        Scored("a", 10, mine: 3.0),
        Scored("b", 5, against: 1.8),
        Scored("e", 0),
        Scored("d", -2, against: 1.5, mine: -6.0),
        Scored("c", -3, against: 1.5),
    ];

    private static ResultsViewModel Show(RankBy rankBy, double? minFraction, bool byTier = false)
    {
        var results = new ResultsViewModel("hint");
        results.SetResults(_items, "");
        results.SetDisplay(rankBy, byTier, minFraction);
        return results;
    }

    private static List<string> Rows(ResultsViewModel results) =>
        results.Entries.OfType<ResultRowViewModel>().Select(row => row.ItemId).ToList();

    [Fact]
    public void TheFormulaListEndsWithTheItemsOnlyTheDataLikes()
    {
        var results = Show(RankBy.Formula, 0);

        Assert.Equal(["a", "b", "c"], Rows(results));
        var header = Assert.IsType<SectionHeaderViewModel>(results.Entries[2]);
        Assert.Equal(ResultsViewModel.DataPicksKey, header.Key);
        Assert.Equal("1", header.CountText);
        Assert.Equal("2 items scoring above 0", results.Summary);

        results.ToggleSection(ResultsViewModel.DataPicksKey);
        Assert.Equal(["a", "b"], Rows(results));
        Assert.True(header.IsCollapsed);
    }

    [Fact]
    public void EveryItemListsNegativesWithNegativeBars()
    {
        var results = Show(RankBy.Formula, null);

        Assert.Equal(["a", "b", "e", "d", "c"], Rows(results));
        Assert.DoesNotContain(results.Entries, entry => entry is SectionHeaderViewModel);
        var c = results.Entries.OfType<ResultRowViewModel>().Single(row => row.ItemId == "c");
        Assert.Equal(-0.3, c.Fraction, 9);
        Assert.True(c.IsNegative);
        Assert.Equal("3", c.ScoreText);
        Assert.True(c.IsStandout);
        Assert.Equal("All 5 items  ·  2 scoring above 0", results.Summary);
    }

    [Fact]
    public void RankingByMatchDataOrdersByTheNettedLifts()
    {
        var results = Show(RankBy.MatchData, 0);

        // b: 1.8, c: 1.5, a: 3 / 3 = 1; d nets below 0 and e has none.
        Assert.Equal(["b", "c", "a"], Rows(results));
        Assert.DoesNotContain(results.Entries, entry => entry is SectionHeaderViewModel);
    }

    [Fact]
    public void AgreementKeepsItemsBothRateAboveZeroRankedByTheLessKeen()
    {
        var results = Show(RankBy.Both, 0);

        // a: min(10/10, 1/1.8) = 0.56; b: min(5/10, 1.8/1.8) = 0.5. c has data but a negative score.
        Assert.Equal(["a", "b"], Rows(results));
        var rows = results.Entries.OfType<ResultRowViewModel>().ToList();
        Assert.All(rows, row => Assert.True(row.HasDataBar));
        Assert.Equal((1.0, 1 / 1.8), (rows[0].Fraction, rows[0].DataFraction));
        Assert.Equal((0.5, 1.0), (rows[1].Fraction, rows[1].DataFraction));
    }

    [Fact]
    public void AgreementSharesOnlyMeasureAgainstItemsBothLike()
    {
        var results = new ResultsViewModel("hint");
        // x is the data's darling but the formula's reject: it mustn't shrink a and b's data shares.
        results.SetResults([Scored("a", 10, mine: 3.0), Scored("b", 5, against: 2.0), Scored("x", -4, against: 6.0)], "");
        results.SetDisplay(RankBy.Both, false, 0);

        var rows = results.Entries.OfType<ResultRowViewModel>().ToList();
        Assert.Equal(["a", "b"], rows.Select(row => row.ItemId));
        Assert.Equal(0.5, rows[0].DataFraction);
        Assert.Equal(1.0, rows[1].DataFraction);
    }

    [Fact]
    public void OnlyTheAgreementRankingDrawsADataBar()
    {
        var results = Show(RankBy.Formula, 0);
        Assert.DoesNotContain(results.Entries.OfType<ResultRowViewModel>(), row => row.HasDataBar);
        results.SetDisplay(RankBy.MatchData, false, 0);
        Assert.DoesNotContain(results.Entries.OfType<ResultRowViewModel>(), row => row.HasDataBar);
        Assert.All(results.Entries.OfType<ResultRowViewModel>(), row => Assert.StartsWith("Data strength", row.BarTip));
    }

    [Fact]
    public void NoHeroesPickedShowsTheHintEvenForEveryItem()
    {
        var results = new ResultsViewModel("hint");
        results.SetResults([Scored("a", 0), Scored("b", 0)], "");
        results.SetDisplay(RankBy.Formula, false, null);

        Assert.True(results.IsEmpty);
        Assert.Equal("hint", results.EmptyHint);
    }

    [Fact]
    public void ClickingTheSelectedRowAgainClearsIt()
    {
        var results = Show(RankBy.Formula, 0);
        var clicked = new List<string?>();
        using var subscription = results.RowClicked.Subscribe(clicked.Add);
        var a = results.Entries.OfType<ResultRowViewModel>().First();

        results.Select(a);
        results.Select(a);

        Assert.Equal(["a", null], clicked);
        Assert.Null(results.SelectedItemId);
        Assert.False(a.IsSelected);
    }
}
