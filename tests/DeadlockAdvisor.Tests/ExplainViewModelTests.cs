using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match.Explain;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

public class ExplainViewModelTests
{
    [Fact]
    public void TheFormulaTotalSaysHowItBecomesTheVerdictsPart()
    {
        Assert.Equal("1683.8 pts ÷ 251 typical = +6.7", ExplainText.FormulaConversion(1683.84, 251.2, 6.703));
        Assert.Equal("-120.0 pts ÷ 240 typical = -0.5", ExplainText.FormulaConversion(-120, 240, -0.5));
    }

    [Fact]
    public void TheTypicalTeamExplainsItselfInTheMatchsNumbers()
    {
        var store = TestStore.Make();
        store.Items["spirit_resist_t1"] = store.Items["spirit_resist_t1"] with { CastOn = Relation.Against };
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("low_hp", Role.Enemy);
        var explain = new ExplainViewModel();

        explain.ShowItem(store, match, "spirit_resist_t1", now: 0);

        // As in ScoringTests: the pair comes to 6 + -4/2 = 4 as targets, and a typical pair to 5/3.
        var typical = explain.Contributions.Single(card => card.Info is not null);
        Assert.Equal("Typical enemy team", typical.HeroName);
        Assert.Contains("a typical team of 2 enemies comes to +1.7", typical.Info);
        Assert.Contains("These enemies come to +4.0 as targets, and +2.3 once the typical team is taken off, "
            + "so these enemies are better targets for it than usual.", typical.Info);
        Assert.Null(typical.NoteTip);
        Assert.All(explain.Contributions.Where(card => card != typical), card => Assert.NotNull(card.NoteTip));
    }

    [Fact]
    public void AHeroWithRankedAndSummedLinesHasOneCard()
    {
        var store = TestStore.Make();
        store.ItemCoefficients[new CoefficientKey("spirit_resist_t1", "max_hp", Relation.Against)] = 1.0;
        store.SetBestTarget("spirit_resist_t1", "deals_spirit_damage_general", Relation.Against, true);
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("low_hp", Role.Enemy);
        var explain = new ExplainViewModel();

        explain.ShowItem(store, match, "spirit_resist_t1", now: 0);

        // low_hp's spirit damage is the 2nd target, -4 at half; its max HP sums at (-4 + 1.3) × 1.
        var lowHp = explain.Contributions.Single(card => card.HeroId == "low_hp");
        Assert.Equal("2nd target ×0.5", lowHp.Note);
        Assert.Contains(ExplainText.PartlyRankedTip, lowHp.NoteTip);
        Assert.Equal([("Deals Spirit Damage", -2.0), ("Has High Max HP", Math.Round(-8.0 / 3, 9))],
            lowHp.Traits.Select(line => (line.TraitName, Math.Round(line.Share.Value, 9))));
        Assert.Equal("(0 − 2 avg) × 2 × 0.5", lowHp.Traits[0].Arithmetic);
        Assert.DoesNotContain("0.5", lowHp.Traits[1].Arithmetic);
        Assert.Equal(-2 - 8.0 / 3, lowHp.Amount.Value, 9);
        Assert.False(lowHp.Traits[0].IsSole);
        // The best target counts in full, which needs no "×1".
        var heavy = explain.Contributions.Single(card => card.HeroId == "heavy_spirit");
        Assert.Equal("best target", heavy.Note);
        Assert.Equal("(5 − 2 avg) × 2", heavy.Traits.Single(line => line.TraitName == "Deals Spirit Damage").Arithmetic);
        // The targets are the ranked lines only: 6 + -4/2.
        Assert.Contains("These enemies come to +4.0 as targets", explain.Contributions.Single(card => card.Info is not null).Info);
    }

    [Fact]
    public void EachEnemySaysHowFocusScaledTheirShare()
    {
        var store = TestStore.Make();
        TestStore.AddLifts(store);
        store.Items["spirit_resist_t1"] = store.Items["spirit_resist_t1"] with { CastOn = Relation.Against };
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("generic", Role.Enemy);
        match.SetFocus("generic", true);
        var explain = new ExplainViewModel();

        explain.ShowItem(store, match, "spirit_resist_t1", now: 0);

        var focused = explain.Contributions.Single(card => card.HeroId == "generic");
        Assert.Equal("2nd target ×0.5 · focused ×1.67", focused.Note);
        Assert.Contains("You focused on Generic, so their share counts ×1.67.", focused.NoteTip);
        Assert.Equal("best target · not focused ×0.33", explain.Contributions.Single(card => card.HeroId == "heavy_spirit").Note);
        Assert.Contains("a typical team of 2 enemies focused the same way comes to",
            explain.Contributions.Single(card => card.Info is not null).Info);

        // The data card weighs each enemy's lift the same way: 1.5 at ×1/3 and -0.25 at ×5/3.
        var data = explain.MatchData!;
        Assert.Equal(1.5 / 3 - 0.25 * 5 / 3, data.Totals.Single(total => total.Word == "enemies").Value.Value, 9);
        var line = data.Lines.Single(line => line.HeroId == "generic");
        Assert.EndsWith(" · focused ×1.67", line.Detail);
        Assert.Equal(-0.25 * 5 / 3, line.Share.Value, 9);
    }

    [Fact]
    public void OnlyHeroCardsShowAPortrait()
    {
        var store = TestStore.Make();
        store.Items["spirit_resist_t1"] = store.Items["spirit_resist_t1"] with { CastOn = Relation.Against };
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("low_hp", Role.Enemy);
        var explain = new ExplainViewModel();

        explain.ShowItem(store, match, "spirit_resist_t1", now: 0);

        Assert.Null(explain.Contributions.Single(card => card.Info is not null).HeroId);
        Assert.Equal(["heavy_spirit", "low_hp"],
            explain.Contributions.Where(card => card.Info is null).Select(card => card.HeroId).Order());
    }
}
