using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match.Explain;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Tests;

/// <summary>
/// An effect your team already has counts for less: what a copy of the same item, or an item of the same build, already gives.
/// In <see cref="TestStore"/> the roster averages 2 on spirit damage (heroes score 5, 0 and 1), so an enemy pair of heavy_spirit and
/// low_hp has weights 6 and -4 from a coefficient of 2.
/// </summary>
public class OverlapTests
{
    private const string Shred = "spirit_resist_t1";
    private const string Upgrade = "shred_t3";
    private const string Unrelated = "other_shred_t2";
    private const string Aura = "aura_t3";
    private const string Part = "part_t1";
    private const string Whole = "whole_t3";
    private const string Spirit = "deals_spirit_damage_general";

    private static readonly EquatableList<TooltipSection> NoSections = Array.Empty<TooltipSection>().ToEquatableList();

    private static ItemEffect Debuff(string itemId, double value) => new(itemId, "SpiritResistReduction", EffectScope.EnemyDebuff, value);

    private static DataStore Store()
    {
        var store = TestStore.Make();
        foreach (var (id, name, tier) in new[] { (Upgrade, "Shred Upgrade", 3), (Unrelated, "Other Shred", 2), (Aura, "Aura", 3), (Part, "Part", 1), (Whole, "Whole", 3) })
            store.Items[id] = new Item(id, name, "spirit", tier);
        store.Heroes["holder"] = new Hero("holder", "Holder");
        store.Heroes["second"] = new Hero("second", "Second");

        // Shred (8) builds into Upgrade (12); Unrelated puts the same debuff on enemies without being part of that build.
        store.ItemTooltips[Upgrade] = new ItemTooltip(Upgrade, NoSections, new[] { Shred }.ToEquatableList());
        store.ItemEffects[Shred] = [Debuff(Shred, 8)];
        store.ItemEffects[Upgrade] = [Debuff(Upgrade, 12)];
        store.ItemEffects[Unrelated] = [Debuff(Unrelated, 8)];
        store.ItemCoefficients[new CoefficientKey(Upgrade, Spirit, Relation.Against)] = 1.0;
        store.ItemCoefficients[new CoefficientKey(Unrelated, Spirit, Relation.Against)] = 2.0;

        // Part (4) builds into Whole (8), which is cast on one enemy at a time.
        store.ItemTooltips[Whole] = new ItemTooltip(Whole, NoSections, new[] { Part }.ToEquatableList());
        store.ItemEffects[Part] = [Debuff(Part, 4)];
        store.ItemEffects[Whole] = [Debuff(Whole, 8)];
        store.Items[Whole] = store.Items[Whole] with { CastOn = Relation.Against };
        store.ItemCoefficients[new CoefficientKey(Whole, Spirit, Relation.Against)] = 2.0;

        // The aura's Bullet Resist is shared (1.7 of 6.7 against spirit damage); its Spirit Resist is its own.
        store.ItemStats[Aura] =
        [
            new ItemStat(Aura, "BulletResist", "Bullet Resist", 17, "%", false),
            new ItemStat(Aura, "TechResist", "Spirit Resist", 20, "%", false),
        ];
        store.ItemStats[Upgrade] = [new ItemStat(Upgrade, "TechResist", "Spirit Resist", 4, "%", false)];
        store.StatRules[new StatRuleKey("BulletResist", Spirit, Relation.Against)] = new StatRule("BulletResist", Spirit, Relation.Against, 0.1, 1);
        store.StatRules[new StatRuleKey("TechResist", Spirit, Relation.Against)] = new StatRule("TechResist", Spirit, Relation.Against, 0.25, 1);
        store.ItemEffects[Aura] = [new ItemEffect(Aura, "BulletResist", EffectScope.Aura, 17)];
        store.RebuildDerived();
        return store;
    }

    private static HeroBuild Holding(params string?[] itemIds) => new(
        itemIds.Select((id, cell) => new BuildCell(cell, CellKind.Item, ShopCategory.Spirit, id, 1)).ToList(), null, DateTimeOffset.UnixEpoch);

    /// <summary>The enemies heavy_spirit and low_hp, and what each ally holds.</summary>
    private static MatchState Match(params (string Hero, string?[] Items)[] allies)
    {
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("low_hp", Role.Enemy);
        foreach (var (hero, items) in allies)
        {
            match.SetRole(hero, Role.Ally);
            match.Builds.Set(hero, Holding(items));
        }
        return match;
    }

    private static double Score(DataStore store, MatchState match, string itemId, bool discount = true)
    {
        var overlap = discount ? TeamOverlap.For(store, match) : null;
        return ItemScoring.ScoreAll(store, ItemScoring.BuildWeightMatrix(store), match, overlap: overlap).Single(item => item.ItemId == itemId).Score;
    }

    [Fact]
    public void AnItemThatBuildsIntoAnotherChainsBothWays()
    {
        var store = Store();

        Assert.Equal([Upgrade, Shred], store.BuildChain(Shred).Order());
        Assert.Equal([Upgrade, Shred], store.BuildChain(Upgrade).Order());
        Assert.Equal([Unrelated], store.BuildChain(Unrelated));
    }

    [Fact]
    public void ACopyOfTheSameItemIsFullyCovered()
    {
        var store = Store();
        var match = Match(("holder", [Shred]));

        Assert.Equal(1.0, TeamOverlap.For(store, match).Coverage(Shred));
        Assert.Equal(2.0, Score(store, match, Shred, discount: false)); // 6 + -4
        Assert.Equal(0.0, Score(store, match, Shred));
    }

    [Fact]
    public void AStrongerItemOnTheBuildAddsOnlyTheDifference()
    {
        var store = Store();

        // Shred (8) already there: Upgrade's 12 is 8/12 covered. Its typed 1 is shared; the Spirit Resist stat's 1 is its own.
        // Typed + stat counts 2 a point of deviation (3 and -2): 6 - 4 = 2; with a third of the typed part left, 4 - 8/3.
        var covered = Match(("holder", [Shred]));
        AssertEx.Close(2.0 / 3, TeamOverlap.For(store, covered).Coverage(Upgrade));
        Assert.Equal(2.0, Score(store, covered, Upgrade, discount: false));
        AssertEx.Close(4.0 / 3, Score(store, covered, Upgrade));

        // The weaker one is wholly covered by the stronger.
        Assert.Equal(1.0, TeamOverlap.For(store, Match(("holder", [Upgrade]))).Coverage(Shred));
    }

    [Fact]
    public void ItemsThatMerelyShareTheEffectStackInFull()
    {
        var store = Store();
        var match = Match(("holder", [Shred, Upgrade]));

        Assert.Equal(0.0, TeamOverlap.For(store, match).Coverage(Unrelated));
        Assert.Equal(Score(store, match, Unrelated, discount: false), Score(store, match, Unrelated));
    }

    [Fact]
    public void AnAurasOwnStatsAreLeftAlone()
    {
        var store = Store();
        var match = Match(("holder", [Aura]));

        // Deviations 3 and -2 sum to 1; 6.7 a point with the aura, and the aura's 1.7 of it gone once it's there.
        AssertEx.Close(6.7, Score(store, match, Aura, discount: false));
        AssertEx.Close(5.0, Score(store, match, Aura));
        var explained = ItemScoring.ExplainItem(store, match, Aura, overlap: TeamOverlap.For(store, match));
        Assert.All(explained.SelectMany(contribution => contribution.Parts), part => Assert.Equal(1.7, part.Covered, 9));
    }

    [Fact]
    public void ABestTargetItemKeepsAveragingZeroOnWhatIsLeft()
    {
        var store = Store();
        var matrix = ItemScoring.BuildWeightMatrix(store);
        var match = Match(("holder", [Part]));
        var overlap = TeamOverlap.For(store, match);
        Assert.Equal(0.5, overlap.Coverage(Whole));

        // Weights 6 and -4 ranked: 6 - 4/2 = 4, less a typical pair's 5/3; half of it is left.
        AssertEx.Close((4 - 5.0 / 3) / 2, ItemScoring.Total(matrix, Whole, ItemScoring.RelevantHeroes(match, overlap: overlap)));

        // Over every pair of enemies from the roster the discounted item still averages 0.
        string[] roster = ["heavy_spirit", "generic", "low_hp"];
        var total = 0.0;
        foreach (var pair in new[] { (0, 1), (0, 2), (1, 2) })
        {
            var enemies = new MatchState();
            enemies.SetRole(roster[pair.Item1], Role.Enemy);
            enemies.SetRole(roster[pair.Item2], Role.Enemy);
            enemies.SetRole("holder", Role.Ally);
            enemies.Builds.Set("holder", Holding(Part));
            total += ItemScoring.Total(matrix, Whole, ItemScoring.RelevantHeroes(enemies, overlap: TeamOverlap.For(store, enemies)));
        }
        AssertEx.Close(0, total / 3);
    }

    [Fact]
    public void NothingChangesWithoutBuildsOrWithTheFilterOff()
    {
        var store = Store();
        var matrix = ItemScoring.BuildWeightMatrix(store);
        var withBuilds = Match(("holder", [Shred]));
        var without = Match();

        Assert.Same(TeamOverlap.None, TeamOverlap.For(store, without));
        foreach (var itemId in new[] { Shred, Upgrade, Unrelated, Aura, Whole })
        {
            Assert.Equal(ItemScoring.ScoreAll(store, matrix, without).Single(item => item.ItemId == itemId).Score, Score(store, without, itemId));
            Assert.Equal(ItemScoring.ScoreAll(store, matrix, withBuilds).Single(item => item.ItemId == itemId).Score, Score(store, withBuilds, itemId, discount: false));
        }
    }

    [Fact]
    public void OnlyWhatYouAndYourAlliesHoldCounts()
    {
        var store = Store();
        var match = Match();
        match.SetRole("generic", Role.Enemy);
        match.Builds.Set("generic", Holding(Shred));
        match.SetRole("holder", Role.Ally);
        match.Builds.Set("holder", Holding(null, null));

        // An enemy's item, and cells the reader wasn't sure of, say nothing about what the team has.
        Assert.Same(TeamOverlap.None, TeamOverlap.For(store, match));

        match.SetRole("second", Role.Self);
        match.Builds.Set("second", Holding(Shred));
        Assert.Equal(1.0, TeamOverlap.For(store, match).Coverage(Shred));
    }

    [Fact]
    public void TwoTeammatesOnOneBuildAreToldOnceAndOneHeroWithBothIsNot()
    {
        var store = Store();

        var apart = TeamOverlap.For(store, Match(("holder", [Shred]), ("second", [Upgrade]))).Shared();
        var shared = Assert.Single(apart);
        Assert.Equal(["holder", "second"], shared.Holders.Select(held => held.HeroId).Order());
        Assert.Equal(("SpiritResistReduction", EffectScope.EnemyDebuff), (shared.Effect, shared.Scope));

        Assert.Empty(TeamOverlap.For(store, Match(("holder", [Shred, Upgrade]))).Shared());
        Assert.Empty(TeamOverlap.For(store, Match(("holder", [Shred]), ("second", [Unrelated]))).Shared());
    }

    [Fact]
    public void TheExplanationSaysWhoCoversWhatAndTheLinesShowTheDiscount()
    {
        var store = Store();
        var match = Match(("holder", [Shred]));
        var overlap = TeamOverlap.For(store, match);

        var note = ExplainText.Overlap(store, match, Upgrade, overlap);

        Assert.Equal("Holder holds Spirit Resist Trinket, which covers 67% of this item's Spirit Resist reduction.\n"
                     + "That part of its score counts for the rest; its other stats count in full.", note);
        Assert.Null(ExplainText.Overlap(store, match, Unrelated, overlap));
        var explained = ItemScoring.ExplainItem(store, match, Upgrade, overlap: overlap);
        AssertEx.Close(Score(store, match, Upgrade), explained.Sum(contribution => contribution.Amount));
        Assert.All(explained.SelectMany(contribution => contribution.Parts), part =>
        {
            AssertEx.Close(1.0 / 3, part.CountedCoefficient - 1.0); // 1 own + a third of the typed 1 left
            Assert.NotNull(ExplainText.CoefficientSource(part));
        });
    }

    [Fact]
    public void TheNoticeNamesTheHeroesAndTheItems()
    {
        var store = Store();
        var match = Match(("holder", [Shred]), ("second", [Upgrade]));

        var shared = Assert.Single(TeamOverlap.For(store, match).Shared());

        Assert.Equal("Holder and Second hold Spirit Resist Trinket and Shred Upgrade: they share one Spirit Resist reduction, and only the stronger counts.",
            ExplainText.SharedNotice(store, match, shared));
        var same = Assert.Single(TeamOverlap.For(store, Match(("holder", [Aura]), ("second", [Aura]))).Shared());
        Assert.Equal("Holder and Second both hold Aura: its Bullet Resist aura doesn't add up, so only one of them is working.",
            ExplainText.SharedNotice(store, match, same));
    }
}
