using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match.Explain;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Tests;

/// <summary>
/// What a hero has built moves their bullet and spirit damage for the match. In this store the roster averages 40 on spirit damage
/// (heroes score 60, 30 and 30) and 30 on bullet damage (10, 50 and 30), and every hero's usual build is a third in each shop except
/// heavy_spirit's: a sixth weapon, two thirds spirit, a sixth vitality.
/// </summary>
public class BuildShiftTests
{
    private const string Bullet = BuildShifts.BulletTrait;
    private const string Spirit = BuildShifts.SpiritTrait;
    private const string Counter = "bullet_counter";
    private const string Marked = "bullet_marked";
    private const string Shield = "spirit_resist_t1";

    private static readonly Patch _patch = new("09-29-2026", 1790726400);

    private static DataStore Store()
    {
        var store = TestStore.Make();
        store.Categories[Spirit] = new Category(Spirit, "Deals Spirit Damage", 0, 100, "");
        store.Categories[Bullet] = new Category(Bullet, "Deals Bullet Damage", 0, 100, "");
        (string Hero, double Spirit, double Bullet)[] scores = [("heavy_spirit", 60, 10), ("low_hp", 30, 50), ("generic", 30, 30)];
        foreach (var (hero, spirit, bullet) in scores)
        {
            store.HeroScores[new ScoreKey(hero, Spirit)] = spirit;
            store.HeroScores[new ScoreKey(hero, Bullet)] = bullet;
        }
        store.Items["gun_t1"] = new Item("gun_t1", "Gun", "weapon", 1, GameId: 11);
        store.Items["orb_t1"] = new Item("orb_t1", "Orb", "spirit", 1, GameId: 12);
        store.Items["vit_t1"] = new Item("vit_t1", "Vit", "vitality", 1, GameId: 13);
        store.Items[Counter] = new Item(Counter, "Bullet Counter", "weapon", 2);
        store.Items[Marked] = new Item(Marked, "Bullet Marked", "weapon", 2);
        store.ItemCoefficients[new CoefficientKey(Counter, Bullet, Relation.Against)] = 2.0;
        store.ItemCoefficients[new CoefficientKey(Marked, Bullet, Relation.Against)] = 2.0;
        store.BestTargetLines.Add(new CoefficientKey(Marked, Bullet, Relation.Against));
        return store;
    }

    private static Halves Bought(long gun, long orb, long vit) => new(
        new() { [11] = new WinTotals(0, gun), [12] = new WinTotals(0, orb), [13] = new WinTotals(0, vit) }, []);

    private static MatchSegment Counts(params (string Hero, long Gun, long Orb, long Vit)[] heroes) =>
        new(_patch, _patch.Start, _patch.Start + 86400, true, _patch.Start + 3 * 86400,
            new SliceCounts(Halves.Empty, new(heroes.Select(row => KeyValuePair.Create(row.Hero, Bought(row.Gun, row.Orb, row.Vit)))), []), [], []);

    private static DataStore WithCounts()
    {
        var store = Store();
        store.MatchSegments = [Counts(("generic", 2000, 2000, 2000), ("low_hp", 2000, 2000, 2000), ("heavy_spirit", 1000, 4000, 1000))];
        return store;
    }

    private static HeroBuild Cells(ShopCategory category, int tier, int count) => new(
        Enumerable.Range(0, count).Select(cell => new BuildCell(cell, CellKind.Item, category, null, tier)).ToList(), null, DateTimeOffset.UnixEpoch);

    private static MatchState Match(HeroBuild? generic = null)
    {
        var match = new MatchState();
        match.SetRole("generic", Role.Enemy);
        match.SetRole("low_hp", Role.Enemy);
        if (generic is not null)
            match.Builds.Set("generic", generic);
        return match;
    }

    private static double Score(DataStore store, MatchState match, string itemId, BuildShifts? shifts) =>
        ItemScoring.ScoreAll(store, ItemScoring.BuildWeightMatrix(store), match, shifts: shifts).Single(item => item.ItemId == itemId).Score;

    [Fact]
    public void TheUsualBuildComesFromThePurchasesAtTheirTierCost()
    {
        var usual = WithCounts().TypicalBuilds;

        Assert.Equal(3, usual.Count);
        AssertEx.Close(1.0 / 3, usual["generic"][ShopCategory.Weapon]);
        AssertEx.Close(2.0 / 3, usual["heavy_spirit"][ShopCategory.Spirit]);
        AssertEx.Close(1.0 / 6, usual["heavy_spirit"][ShopCategory.Vitality]);
    }

    [Fact]
    public void AHeroWithTooFewPurchasesHasNoUsualBuild()
    {
        var store = Store();
        store.MatchSegments = [Counts(("generic", 1000, 1000, 1000)), ];

        Assert.Empty(store.TypicalBuilds);
        Assert.True(BuildShifts.For(store, Match(Cells(ShopCategory.Weapon, 1, 6))).IsEmpty);
    }

    [Fact]
    public void ACountsWeightIsItsPatchesShareOfTheLifts()
    {
        var store = Store();
        var older = Counts(("generic", 3000, 1500, 1500)) with { Patch = new Patch("09-01-2026", 1788220800) };
        var newer = Counts(("generic", 1500, 3000, 1500));
        store.MatchSegments = [newer, older];
        store.MatchMeta = new System.Text.Json.Nodes.JsonObject
        {
            ["segments"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["start"] = newer.Patch.Start, ["share"] = 0.75 },
                new System.Text.Json.Nodes.JsonObject { ["start"] = older.Patch.Start, ["share"] = 0.25 }),
        };

        AssertEx.Close(0.75 * 0.25 + 0.25 * 0.5, store.TypicalBuilds["generic"][ShopCategory.Weapon]);
    }

    [Fact]
    public void AHeroAtTheirUsualBuildMovesNothing()
    {
        var store = WithCounts();
        // Two cells of each shop at tier 1 is a third in each, exactly the usual.
        var even = new HeroBuild(
            Enum.GetValues<ShopCategory>().SelectMany((category, shop) => Enumerable.Range(0, 2).Select(i => new BuildCell(shop * 2 + i, CellKind.Item, category, null, 1))).ToList(),
            null, DateTimeOffset.UnixEpoch);
        var match = Match(even);

        Assert.True(BuildShifts.For(store, match).IsEmpty);
        Assert.Equal(Score(store, match, Counter, null), Score(store, match, Counter, BuildShifts.For(store, match)));
    }

    [Fact]
    public void AWeaponHeavyBuildShiftsTheTraitsByTheRampedLean()
    {
        var store = WithCounts();
        // Six tier 1 weapon cells: 4,800 souls, so 0.4 of the way up the ramp, and all weapon against a third: a lean of 1.
        var match = Match(Cells(ShopCategory.Weapon, 1, 6));

        var shift = BuildShifts.For(store, match).Of("generic")!;

        AssertEx.Close(1.0, shift.Lean);
        AssertEx.Close(4800.0 / BuildShifts.FullRampSouls, shift.Ramp);
        var points = BuildShifts.Alpha * shift.Ramp;
        AssertEx.Close(points, shift.PointsOn(Bullet));
        AssertEx.Close(-points, shift.PointsOn(Spirit));
        Assert.Null(BuildShifts.For(store, match).Of("low_hp"));
    }

    [Fact]
    public void TheScoreFollowsTheShiftedTraitTimesTheItemsCoefficient()
    {
        var store = WithCounts();
        var match = Match(Cells(ShopCategory.Weapon, 1, 6));
        var shifts = BuildShifts.For(store, match);
        var points = shifts.Of("generic")!.PointsOn(Bullet);

        // Bullet counter, 2 a point against bullet damage: generic sits at the average (0) and low_hp 20 above it (40).
        AssertEx.Close(40, Score(store, match, Counter, null));
        AssertEx.Close(40 + 2 * points, Score(store, match, Counter, shifts));
        // The shield is 2 a point of spirit damage against: both enemies are 10 under the average (-40); generic loses the points more.
        AssertEx.Close(-40, Score(store, match, Shield, null));
        AssertEx.Close(-40 - 2 * points, Score(store, match, Shield, shifts));
    }

    [Fact]
    public void TheShiftReachesTheBestTargetsBeforeTheyAreRanked()
    {
        var store = WithCounts();
        var match = Match(Cells(ShopCategory.Weapon, 1, 6));
        var shifts = BuildShifts.For(store, match);
        var points = shifts.Of("generic")!.PointsOn(Bullet);

        // Best target counts in full and the second ×0.5: generic goes from 0 to 2 × points, still behind low_hp's 40, so it adds half.
        AssertEx.Close(Score(store, match, Marked, null) + points, Score(store, match, Marked, shifts));
    }

    [Fact]
    public void AnInventoryOfTheFullRampCountsInFullAndTheScaleKeepsTheScoreInside()
    {
        var store = WithCounts();
        // Two tier 4 weapon cells are 12,800 souls: the whole ramp.
        var full = BuildShifts.Shift(store, "generic", Cells(ShopCategory.Weapon, 4, 2), store.TypicalBuilds["generic"], BuildShifts.MedianCosts(store))!;
        Assert.Equal(1.0, full.Ramp);
        AssertEx.Close(BuildShifts.Alpha, full.PointsOn(Bullet));

        // A hero who usually builds none of either: a lean of 2 would be twice Alpha, and spirit can't go below its scale's 0 from 30.
        var usual = new Dictionary<ShopCategory, double> { [ShopCategory.Weapon] = 0, [ShopCategory.Spirit] = 1, [ShopCategory.Vitality] = 0 };
        var past = BuildShifts.Shift(store, "generic", Cells(ShopCategory.Weapon, 4, 2), usual, BuildShifts.MedianCosts(store))!;
        AssertEx.Close(2.0, past.Lean);
        AssertEx.Close(2 * BuildShifts.Alpha, past.PointsOn(Bullet));
        AssertEx.Close(-30, past.PointsOn(Spirit));
    }

    [Fact]
    public void AnUnratedTraitIsLeftAlone()
    {
        var store = WithCounts();
        store.HeroScores.Remove(new ScoreKey("generic", Spirit));

        var shift = BuildShifts.For(store, Match(Cells(ShopCategory.Weapon, 1, 6))).Of("generic")!;

        Assert.Equal([Bullet], shift.Traits.Select(trait => trait.CategoryId));
    }

    [Fact]
    public void ACellWhoseTierWasntReadCountsForItsShopsMedianItem()
    {
        var store = WithCounts();
        var unread = new HeroBuild([new BuildCell(0, CellKind.Item, ShopCategory.Weapon, null, null)], null, DateTimeOffset.UnixEpoch);

        var shift = BuildShifts.Shift(store, "generic", unread, store.TypicalBuilds["generic"], BuildShifts.MedianCosts(store))!;

        // The store's weapon items are two of tier 1 (the irrelevant one and the gun) and two of tier 2: the upper median is 1,600.
        Assert.Equal(1600, shift.Spent);
    }

    [Fact]
    public void WithoutCountsOrBuildsNothingMoves()
    {
        var store = Store();
        Assert.True(BuildShifts.For(store, Match(Cells(ShopCategory.Weapon, 1, 6))).IsEmpty);
        Assert.True(BuildShifts.For(WithCounts(), Match()).IsEmpty);
    }

    [Fact]
    public void TheExplanationShowsTheRatedScoreAndWhereTheBuildMovedIt()
    {
        var store = WithCounts();
        var match = Match(Cells(ShopCategory.Weapon, 1, 6));
        var shifts = BuildShifts.For(store, match);
        var points = shifts.Of("generic")!.PointsOn(Bullet);

        var generic = ItemScoring.ExplainItem(store, match, Counter, shifts: shifts).Single(contribution => contribution.HeroId == "generic");
        var part = Assert.Single(generic.Parts);

        Assert.Equal(30, part.HeroScore);
        AssertEx.Close(30 + points, part.CountedScore);
        AssertEx.Close(2 * points, generic.Amount);
        Assert.StartsWith("30 → ", ExplainText.Shifted(part, generic.Shift)!, StringComparison.Ordinal);
        Assert.EndsWith("(built weapon)", ExplainText.Shifted(part, generic.Shift), StringComparison.Ordinal);
        // The explanation adds up to the score.
        var total = ItemScoring.ExplainItem(store, match, Counter, shifts: shifts).Sum(contribution => contribution.Amount);
        AssertEx.Close(Score(store, match, Counter, shifts), total);
    }

    [Fact]
    public void ALineUpWithoutShiftsScoresAsBefore()
    {
        var store = WithCounts();
        var match = Match(Cells(ShopCategory.Weapon, 1, 6));
        var matrix = ItemScoring.BuildWeightMatrix(store);

        Assert.Equal(
            ItemScoring.ScoreAll(store, matrix, match).Select(item => item.Score),
            ItemScoring.ScoreAll(store, matrix, match, shifts: BuildShifts.None).Select(item => item.Score));
    }
}
