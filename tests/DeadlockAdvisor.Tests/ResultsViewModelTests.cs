using Avalonia.Media;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match.Results;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Theme;

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

    private static ResultsViewModel Show(RankBy rankBy, double? minFraction, bool byTier = false, BlendScale? blend = null)
    {
        var results = new ResultsViewModel("hint");
        results.SetResults(_items, "", blend is { } scale ? () => scale : null);
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
        Assert.Equal("2 items above 0", results.Summary);

        results.ToggleSection(ResultsViewModel.DataPicksKey);
        Assert.Equal(["a", "b"], Rows(results));
        Assert.True(header.IsCollapsed);
    }

    [Fact]
    public void OnlyTheEditorsHearADataPickMayMeanAMissingRule()
    {
        var results = Show(RankBy.Formula, 0);
        SectionHeaderViewModel Picks() => results.Entries.OfType<SectionHeaderViewModel>().Single();
        Assert.Equal(ResultsViewModel.DataPicksNote, Picks().Note);
        results.ToggleSection(ResultsViewModel.DataPicksKey);

        results.ShowsEditors = true;

        Assert.Equal(ResultsViewModel.DataPicksEditorNote, Picks().Note);
        Assert.True(Picks().IsCollapsed);
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
        Assert.Equal("3.0", c.Score.Text);
        Assert.True(c.IsStandout);
        Assert.Equal("All 5 items  ·  2 above 0", results.Summary);
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
    public void FormulaPlusDataRanksByTheSumInCommonUnits()
    {
        // A formula unit of 5 points and a data unit of 1: a 2 + 1 = 3, b 1 + 1.8 = 2.8, c -0.6 + 1.5 = 0.9,
        // d -0.4 - 0.5 = -0.9. c's data outweighs its formula score, so it's listed.
        var results = Show(RankBy.Both, 0, blend: new BlendScale(5, 1));

        Assert.Equal(["a", "b", "c"], Rows(results));
        var rows = results.Entries.OfType<ResultRowViewModel>().ToList();
        Assert.All(rows, row => Assert.True(row.HasDataBar));
        // Both bars on one scale, the largest part on screen: a's formula 2.
        Assert.Equal((1.0, 0.5), (rows[0].Fraction, rows[0].DataFraction));
        Assert.Equal(-0.3, rows[2].Fraction, 9);
        Assert.Equal(0.75, rows[2].DataFraction, 9);
        Assert.Equal("3.0", rows[0].Score.Text);
        Assert.Equal("3 items above 0", results.Summary);
    }

    [Fact]
    public void LabelsColourEachOpinionLikeItsBar()
    {
        (string, Color?)[] expected =
        [
            ("Formula", Palette.Formula), (" +8.0 · no ", null), ("match data", Palette.Data), (" · the ", null), ("data", Palette.Data),
        ];

        Assert.Equal(expected, OpinionTerms.Spans("Formula +8.0 · no match data · the data").Select(span => (span.Text, span.Color)));
        Assert.Empty(OpinionTerms.Spans(null));
        Assert.Equal([new TextSpan("Metadata")], OpinionTerms.Spans("Metadata"));
    }

    [Fact]
    public void AnItemTheFormulaHasNothingToSayAboutRanksOnItsData()
    {
        var results = new ResultsViewModel("hint");
        results.SetResults([Scored("a", 10), Scored("s", 0, against: 1.2)], "", () => new BlendScale(10, 1));
        results.SetDisplay(RankBy.Both, false, 0);

        // s: 0 + 1.2; a: 1 + 0.
        Assert.Equal(["s", "a"], Rows(results));
    }

    [Fact]
    public void OpposedOpinionsAreMarkedOnlyWhenBlending()
    {
        var results = Show(RankBy.Both, null, blend: new BlendScale(1, 1));

        // c: formula -3 against data +1.5; a: 10 and 1, the same way; d: -2 and -0.5, also the same way.
        var rows = results.Entries.OfType<ResultRowViewModel>().ToDictionary(row => row.ItemId);
        Assert.Equal(["c"], rows.Values.Where(row => row.Disagrees).Select(row => row.ItemId));

        results.SetDisplay(RankBy.Formula, false, null);
        Assert.DoesNotContain(results.Entries.OfType<ResultRowViewModel>(), row => row.Disagrees);
    }

    [Fact]
    public void OnlyTheFormulaPlusDataRankingDrawsADataBar()
    {
        var results = Show(RankBy.Formula, 0);
        Assert.DoesNotContain(results.Entries.OfType<ResultRowViewModel>(), row => row.HasDataBar);
        results.SetDisplay(RankBy.MatchData, false, 0);
        Assert.DoesNotContain(results.Entries.OfType<ResultRowViewModel>(), row => row.HasDataBar);
        Assert.All(results.Entries.OfType<ResultRowViewModel>(), row => Assert.StartsWith("Data strength", row.BarTip));
    }

    [Fact]
    public void ARarelyBuiltItemSaysWhyItsEnemyLiftsCountLess()
    {
        var results = new ResultsViewModel("hint");
        results.SetResults([Scored("a", 10, against: 0.4) with { BuildRatio = 0.1 }, Scored("b", 5, against: 1.0) with { BuildRatio = 0.1 }, Scored("c", 1) with { BuildRatio = 0.1 }], "note");
        results.SetDisplay(RankBy.Formula, false, null);

        var rows = results.Entries.OfType<ResultRowViewModel>().ToList();
        Assert.True(rows[0].IsRarelyBuilt);
        Assert.Equal("Your hero builds this 1/10 as often as the average player, so the gains against the enemies count ×0.40: "
                     + "they mostly come from other heroes' players.\n\nnote", rows[0].DataTip);
        // Without data there's nothing that counts for less.
        Assert.False(rows[2].IsRarelyBuilt);
        Assert.Equal("note", rows[2].DataTip);
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
