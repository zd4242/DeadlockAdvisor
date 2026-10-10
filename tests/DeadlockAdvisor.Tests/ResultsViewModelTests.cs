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
        Assert.Equal("2 items suit this match", results.Summary);

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
    public void OnlyTheEditorsSeeTheCutoffsNumber()
    {
        // Cut at 60% of a's 10, so b's 5 is left out.
        var results = Show(RankBy.Formula, 0.6);
        Assert.Equal("Best 1 of 2 that suit this match", results.Summary);

        results.ShowsEditors = true;

        Assert.Equal("1 of 2 above 0  ·  cutoff 6", results.Summary);
    }

    [Fact]
    public void TierSectionsCutEachTierAgainstItsOwnBest()
    {
        var results = new ResultsViewModel("hint");
        results.SetResults(
        [
            Scored("a", 10, tier: 4) with { Cost = 6400 },
            Scored("b", 3, tier: 4) with { Cost = 6400 },
            Scored("c", 2, tier: 1) with { Cost = 800 },
            Scored("d", 0.5, tier: 1) with { Cost = 800 },
        ], "");

        // Flat, everything is cut at 40% of a's 10, which leaves no tier 1 item; each row names its tier once asked to.
        results.SetDisplay(RankBy.Formula, false, 0.4);
        Assert.Equal(["a"], Rows(results));
        var a = results.Entries.OfType<ResultRowViewModel>().Single();
        Assert.False(a.ShowsTier);
        results.ShowsTiers = true;
        Assert.True(a.ShowsTier);
        Assert.Equal("T4 · 6.4k", a.TierText);

        // In tiers, tier 1 is cut at 40% of c's 2, so c stays; the header names the tier and its price instead of the row.
        results.SetDisplay(RankBy.Formula, true, 0.4);
        Assert.Equal(["c", "a"], Rows(results));
        Assert.Equal(["TIER 1 · 800", "TIER 4 · 6,400"], results.Entries.OfType<SectionHeaderViewModel>().Select(header => header.Title));
        Assert.All(results.Entries.OfType<ResultRowViewModel>(), row => Assert.False(row.ShowsTier));
        Assert.Equal("Best 2 of 4 that suit this match", results.Summary);

        results.ShowsEditors = true;
        Assert.Equal("2 of 4 above 0  ·  cutoff 40% of each tier's best", results.Summary);
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
        Assert.Equal("All 5 items  ·  2 suit this match", results.Summary);
    }

    [Fact]
    public void RankingByMatchDataOrdersByTheNettedLifts()
    {
        var results = Show(RankBy.MatchData, 0);

        // b: 1.8, c: 1.5, a: 3 / 3 = 1; d nets below 0 and e has none.
        Assert.Equal(["b", "c", "a"], Rows(results));
        Assert.DoesNotContain(results.Entries, entry => entry is SectionHeaderViewModel);
        // The number and the bar are the data strength, not the formula score, in the data's colour.
        var rows = results.Entries.OfType<ResultRowViewModel>().ToList();
        Assert.Equal(["1.8", "1.5", "1.0"], rows.Select(row => row.Score.Text));
        Assert.All(rows, row => Assert.Equal(Palette.Data, row.BarColor));

        results.SetDisplay(RankBy.Formula, false, 0);
        Assert.All(results.Entries.OfType<ResultRowViewModel>(), row => Assert.Equal(Palette.Formula, row.BarColor));
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
        Assert.Equal("3 items suit this match", results.Summary);
    }

    [Fact]
    public void LabelsColourEachOpinionLikeItsBar()
    {
        (string, Color?)[] expected =
        [
            ("Advisor rating", Palette.Formula), (" +8.0 · no ", null), ("match results", Palette.Data), (" · the ", null),
            ("formula", Palette.Formula), (" and ", null), ("match data", Palette.Data),
        ];

        Assert.Equal(expected,
            OpinionTerms.Spans("Advisor rating +8.0 · no match results · the formula and match data").Select(span => (span.Text, span.Color)));
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
        Assert.Contains("the match results rate this item well, the advisor rating poorly", rows["c"].DisagreeTip);
        Assert.EndsWith("Filters → \"Hide items the advisor rating and results disagree on\" leaves these items out of the list.", rows["c"].DisagreeTip);
        Assert.Null(rows["a"].DisagreeTip);

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
        Assert.All(results.Entries.OfType<ResultRowViewModel>(), row => Assert.StartsWith("Match results strength", row.BarTip));
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
                     + "they mostly come from other heroes' players.\n"
                     + "Filters → \"Hide items your hero rarely builds\" leaves these items out of the list.", rows[0].RarelyBuiltTip);
        Assert.Equal("note", rows[0].DataTip);
        // Without data there's nothing that counts for less.
        Assert.False(rows[2].IsRarelyBuilt);
        Assert.Null(rows[2].RarelyBuiltTip);
    }

    [Fact]
    public void HidingRarelyBuiltItemsLeavesThemOutBeforeTheCutoff()
    {
        var results = new ResultsViewModel("hint");
        results.SetResults([Scored("a", 10, against: 0.4) with { BuildRatio = 0.1 }, Scored("b", 5, against: 1.0), Scored("c", 3)], "note");

        results.SetDisplay(RankBy.Formula, false, 0.5, hideRarelyBuilt: true);

        // "b" is the best listed item, so the cutoff is half of its score rather than of the hidden "a"'s.
        Assert.Equal(["b", "c"], results.Entries.OfType<ResultRowViewModel>().Select(row => row.ItemId));
        Assert.Equal("2 items suit this match", results.Summary);
    }

    [Fact]
    public void HidingDisagreedItemsLeavesThemOutBeforeTheCutoffOnlyWhenBlending()
    {
        var results = new ResultsViewModel("hint");
        results.SetResults([Scored("a", 10, against: -2.0), Scored("b", 4, against: 0.5), Scored("c", 3)], "", () => new BlendScale(1, 1));

        results.SetDisplay(RankBy.Both, false, 0.5, hideDisagreed: true);

        // The formula rates "a" 10 and the data -2. "b" is then the best listed item at 4.5, so the cutoff is half of that rather than of a's 8.
        Assert.Equal(["b", "c"], Rows(results));
        Assert.Equal("2 items suit this match", results.Summary);

        // Ranked by the formula alone, there's no second opinion on the same scale to disagree with.
        results.SetDisplay(RankBy.Formula, false, 0.5, hideDisagreed: true);
        Assert.Equal(["a"], Rows(results));
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
    public void ClearingTheHeroesDropsThePickEvenForEveryItem()
    {
        var results = Show(RankBy.Formula, null);
        results.Select(results.Entries.OfType<ResultRowViewModel>().First());

        results.SetResults([Scored("a", 0), Scored("b", 0)], "");

        Assert.True(results.IsEmpty);
        Assert.Null(results.SelectedItemId);
    }

    [Fact]
    public void ASearchFindsAnItemTheCutoffHidesAndPicksASingleMatch()
    {
        // Cut at 60% of a's 10, so b's 5 is left out of the plain list.
        var results = Show(RankBy.Formula, 0.6);
        var clicked = new List<string?>();
        using var subscription = results.RowClicked.Subscribe(clicked.Add);

        results.SearchText = "b";

        Assert.Equal(["b"], Rows(results));
        Assert.Equal("1 item match", results.Summary);
        Assert.Equal("b", results.SelectedItemId);
        Assert.Equal(["b"], clicked);

        // Clearing it brings the plain list back, which has no b to keep picked.
        results.SearchText = "";

        Assert.Equal(["a", "c"], Rows(results));
        Assert.Null(results.SelectedItemId);
        Assert.Equal(["b", null], clicked);
    }

    [Fact]
    public void ASearchSaysWhyTheFiltersLeaveAMatchOutOfTheList()
    {
        var results = new ResultsViewModel("hint");
        results.SetResults([Scored("a", 10, against: 0.4) with { BuildRatio = 0.1 }, Scored("b", 5, against: 1.0), Scored("c", 1)], "");
        results.SetDisplay(RankBy.Formula, false, 0.5, hideRarelyBuilt: true);
        Assert.Equal(["b"], Rows(results));
        string? Reason(string itemId) => results.Entries.OfType<ResultRowViewModel>().Single(row => row.ItemId == itemId).HiddenReason;

        results.SearchText = "a";
        Assert.Equal("Left out of the list: your hero rarely builds it (Filters).", Reason("a"));
        Assert.Equal("a", results.SelectedItemId);

        results.SearchText = "c";
        Assert.Equal("Left out of the list: below the cutoff (Filters).", Reason("c"));

        // b is in the plain list, so there's nothing to say about it.
        results.SearchText = "b";
        Assert.Null(Reason("b"));

        results.SearchText = "";
        Assert.Null(Reason("b"));
    }

    [Fact]
    public void ASearchPicksItsOnlyMatchAndKeepsThePickWhenNothingMatches()
    {
        var results = Show(RankBy.Formula, null);
        results.Select(results.Entries.OfType<ResultRowViewModel>().First());

        results.SearchText = "d";

        // d is the only match, so it takes the pick.
        Assert.Equal(["d"], Rows(results));
        Assert.Equal("d", results.SelectedItemId);

        // Nothing matches: the list says so, and the pick stays where it was.
        results.SearchText = "zzz";

        Assert.True(results.IsEmpty);
        Assert.Equal("No item matches \"zzz\".", results.EmptyHint);
        Assert.Equal("d", results.SelectedItemId);
    }

    [Fact]
    public void ASearchListsMatchesInCollapsedSections()
    {
        var results = Show(RankBy.Formula, null, byTier: true);
        results.ToggleSection("tier1");
        Assert.Empty(Rows(results));

        results.SearchText = "a";
        Assert.Equal(["a"], Rows(results));

        results.SearchText = "";
        Assert.Empty(Rows(results));
    }

    [Fact]
    public void ResettingTheSearchEmptiesAndClosesIt()
    {
        var results = Show(RankBy.Formula, 0);
        results.IsSearchOpen = true;
        results.SearchText = "b";

        results.ResetSearch();

        Assert.Equal("", results.SearchText);
        Assert.False(results.IsSearchOpen);
        Assert.Equal(["a", "b", "c"], Rows(results));
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
