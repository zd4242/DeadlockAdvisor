using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

/// <summary>
/// What an item pushes its holder's damage toward, worked out from its stats and the enemy debuffs it puts on through a table of votes
/// (<see cref="StatLean"/>), fixed by hand where those don't say, and its shop's colour where the table is silent.
/// </summary>
public class ItemLeanTests
{
    private const string Gun = "gun_t2";
    private const string Shredder = "shredder_t2";
    private const string Orb = "orb_t2";
    private const string Plate = "plate_t1";

    private static ItemStat Stat(string itemId, string stat, bool conditional = false) =>
        new(itemId, stat, stat + " label", 10, "", conditional);

    private static DataStore Store(bool withTable = true)
    {
        var store = TestStore.Make();
        store.Items[Gun] = new Item(Gun, "Gun", "weapon", 2);
        store.Items[Shredder] = new Item(Shredder, "Shredder", "spirit", 2);
        store.Items[Orb] = new Item(Orb, "Orb", "spirit", 2);
        store.Items[Plate] = new Item(Plate, "Plate", "vitality", 1);
        store.ItemStats[Gun] = [Stat(Gun, "ShopWeaponDamage"), Stat(Gun, "BonusFireRate", conditional: true)];
        store.ItemStats[Shredder] = [Stat(Shredder, "ShopSpiritPower")];
        store.ItemEffects[Shredder] = [new ItemEffect(Shredder, "BulletResistReduction", EffectScope.EnemyDebuff, 10)];
        store.ItemStats[Orb] = [Stat(Orb, "ShopSpiritPower"), Stat(Orb, "TechPower"), Stat(Orb, "TechPower", conditional: true)];
        store.ItemStats[Plate] = [Stat(Plate, "ShopBaseHealth"), Stat(Plate, "BonusHealth")];
        if (withTable)
        {
            foreach (var (stat, lean, factor) in new (string, double, double)[]
                     {
                         ("ShopWeaponDamage", 0.25, 1), ("BonusFireRate", 0.5, 0.5), ("BulletResistReduction", 0.75, 1), ("ShopSpiritPower", -0.25, 1),
                         ("TechPower", -0.5, 0.5), ("ShopBaseHealth", 0, 1), ("BonusHealth", 0, 1),
                     })
                store.StatLeans[stat] = new StatLean(stat, lean, factor);
        }
        store.RebuildDerived();
        return store;
    }

    [Fact]
    public void AnItemsLeanIsTheSumOfItsStatsVotesWithConditionalOnesDiscounted()
    {
        var lean = Store().LeanOf(Gun);

        // 0.25 for the weapon shop's bonus and half of the fire rate's 0.5, which only counts some of the time.
        Assert.Equal(0.5, lean.Lean);
        Assert.Equal(LeanSource.Derived, lean.Source);
        Assert.Equal(["ShopWeaponDamage", "BonusFireRate"], lean.Votes.Select(vote => vote.Key));
    }

    [Fact]
    public void AStatCountsOnceHoweverManyTimesTheItemHasIt()
    {
        // Spirit power twice, always and sometimes: the always-on one counts, once.
        var lean = Store().LeanOf(Orb);

        Assert.Equal(-0.75, lean.Lean);
        Assert.Equal(2, lean.Votes.Count);
    }

    [Fact]
    public void AnEnemyDebuffVotesSoAnItemOutsideItsShopsColourLeansTheOtherWay()
    {
        var lean = Store().LeanOf(Shredder);

        // A spirit item, but what it does is lower Bullet Resist: -0.25 and +0.75.
        Assert.Equal(0.5, lean.Lean);
        Assert.Contains(lean.Votes, vote => vote is { Key: "BulletResistReduction", Vote: 0.75 });
    }

    [Fact]
    public void AnAuraDoesNotVote()
    {
        var store = Store();
        store.ItemEffects[Gun] = [new ItemEffect(Gun, "BulletResistReduction", EffectScope.Aura, 17)];
        store.RebuildDerived();

        Assert.Equal(0.5, store.LeanOf(Gun).Lean);
    }

    [Fact]
    public void ANeutralStatIsAVoteOfNothingNotASilence()
    {
        var lean = Store().LeanOf(Plate);

        Assert.Equal((0.0, LeanSource.Derived), (lean.Lean, lean.Source));
    }

    [Fact]
    public void TheSumStaysWithinOneEitherWay()
    {
        var store = Store();
        store.ItemStats[Gun] = [Stat(Gun, "ShopWeaponDamage"), Stat(Gun, "BonusFireRate"), Stat(Gun, "BulletResistReduction")];
        store.ItemStats[Orb] = [Stat(Orb, "TechPower"), Stat(Orb, "ShopSpiritPower"), Stat(Orb, "TechPower", conditional: true)];
        store.StatLeans["TechPower"] = new StatLean("TechPower", -1.5);
        store.RebuildDerived();

        Assert.Equal(1.0, store.LeanOf(Gun).Lean);
        Assert.Equal(-1.0, store.LeanOf(Orb).Lean);
    }

    [Fact]
    public void WithoutAnyVoteTheShopsColourStandsIn()
    {
        var store = Store(withTable: false);

        Assert.Equal((1.0, LeanSource.Shop), (store.LeanOf(Gun).Lean, store.LeanOf(Gun).Source));
        Assert.Equal(-1.0, store.LeanOf(Shredder).Lean);
        Assert.Equal(0.0, store.LeanOf(Plate).Lean);

        // A table that has no row for any of an item's stats says nothing about it either.
        store.StatLeans["TechPower"] = new StatLean("TechPower", -0.5);
        store.RebuildDerived();
        Assert.Equal(LeanSource.Shop, store.LeanOf(Gun).Source);
        Assert.Equal(LeanSource.Derived, store.LeanOf(Orb).Source);
    }

    [Fact]
    public void AnOverrideReplacesWhatTheStatsSayAndClearingItBringsThemBack()
    {
        var store = Store();

        Assert.True(store.SetItemLean(Shredder, 1.0, "Shreds Bullet Resist: a bullet item"));
        Assert.Equal((1.0, LeanSource.Override), (store.LeanOf(Shredder).Lean, store.LeanOf(Shredder).Source));
        Assert.False(store.SetItemLean(Shredder, 1.0, "Shreds Bullet Resist: a bullet item"));

        Assert.True(store.SetItemLean(Shredder, 4.0, "too much"));
        Assert.Equal(1.0, store.LeanOf(Shredder).Lean);

        Assert.True(store.ClearItemLean(Shredder));
        Assert.False(store.ClearItemLean(Shredder));
        Assert.Equal((0.5, LeanSource.Derived), (store.LeanOf(Shredder).Lean, store.LeanOf(Shredder).Source));
    }

    [Fact]
    public void ALeanIsWorkedOutAgainAfterTheStatsOrTheTableChange()
    {
        var store = Store();
        Assert.Equal(0.5, store.LeanOf(Gun).Lean);

        store.StatLeans["BonusFireRate"] = new StatLean("BonusFireRate", 1.0, 1.0);
        Assert.Equal(0.5, store.LeanOf(Gun).Lean);
        store.RebuildDerived();

        Assert.Equal(1.0, store.LeanOf(Gun).Lean);
    }

    [Fact]
    public void AnItemTheStoreDoesntHaveIsNeutral()
    {
        Assert.Equal((0.0, LeanSource.Shop), (Store().LeanOf("nowhere").Lean, Store().LeanOf("nowhere").Source));
    }

    [Fact]
    public void TheTablesReadAndWriteBackUnchanged()
    {
        using var data = new TempDirectory();
        var statLeans = "stat,lean,conditional_factor,note\r\nBonusFireRate,0.5,0.65,\"Fire rate, which charges up\"\r\nTechPower,-0.5,1,\r\n";
        var overrides = "item_id,lean,reason\r\nbullet_resist_shredder,0.75,\"Lowers Bullet Resist, so a bullet item\"\r\n";
        File.WriteAllText(data.File(DataStore.StatLeansFile), statLeans);
        File.WriteAllText(data.File(DataStore.ItemLeanOverridesFile), overrides);
        var store = new DataStore(data.Path);

        store.LoadStatLeans();
        store.LoadItemLeanOverrides();
        store.SaveStatLeans();
        store.SaveItemLeanOverrides();

        Assert.Equal(0.65, store.StatLeans["BonusFireRate"].ConditionalFactor);
        Assert.Equal("Lowers Bullet Resist, so a bullet item", store.ItemLeanOverrides["bullet_resist_shredder"].Reason);
        Assert.Equal(statLeans, File.ReadAllText(data.File(DataStore.StatLeansFile)));
        Assert.Equal(overrides, File.ReadAllText(data.File(DataStore.ItemLeanOverridesFile)));
    }

    [Fact]
    public void ADataFolderWithoutTheTablesStillLoads()
    {
        using var data = new TempDirectory();
        foreach (var file in Directory.GetFiles(Golden.DataDir).Where(file => !Path.GetFileName(file).StartsWith("stat_leans", StringComparison.Ordinal)
                                                                                && !Path.GetFileName(file).StartsWith("item_lean", StringComparison.Ordinal)))
            File.Copy(file, Path.Combine(data.Path, Path.GetFileName(file)));

        var store = DataStore.Load(data.Path);

        Assert.Empty(store.StatLeans);
        Assert.Equal(1.0, store.LeanOf("hollow_point").Lean);
    }

    [Fact]
    public void TheReviewListsItemsWhoseStatsSendThemAwayFromTheirShop()
    {
        var store = Store();
        store.Items[Shredder] = store.Items[Shredder] with { Tier = 2 };

        var lines = LeanReview.Lines(store);

        Assert.Contains(lines, line => line.Contains("Items whose stats send them away from their shop", StringComparison.Ordinal));
        Assert.Contains("  T2 Shredder (spirit): +0.50 from ShopSpiritPower label -0.25, BulletResistReduction +0.75", lines);
        Assert.DoesNotContain(lines, line => line.Contains("Orb", StringComparison.Ordinal));
    }

    [Fact]
    public void TheReviewSaysWhichOverridesTheStatsNowAgreeWith()
    {
        var store = Store();
        store.SetItemLean(Shredder, 0.5, "was needed before the effects were synced");
        store.SetItemLean(Gun, -1.0, "its stats mislead");

        var lines = LeanReview.Lines(store);

        Assert.Contains("  Shredder: +0.50, \"was needed before the effects were synced\" (the stats say the same now: remove it)", lines);
        Assert.Contains("  Gun: -1.00, \"its stats mislead\" (the stats say +0.50)", lines);
    }

    [Fact]
    public void TheReviewNamesTheStatsNobodyHasVotedOn()
    {
        var store = Store();
        store.ItemStats[Gun] = [.. store.ItemStats[Gun], Stat(Gun, "BrandNewStat")];
        store.ItemStats[Orb] = [.. store.ItemStats[Orb], Stat(Orb, "BrandNewStat")];
        store.ItemEffects[Orb] = [new ItemEffect(Orb, "NewDebuff", EffectScope.EnemyDebuff, 5), new ItemEffect(Orb, "NewAura", EffectScope.Aura, 5)];

        var line = Assert.Single(LeanReview.Lines(store), line => line.StartsWith("Stats and debuffs with no row", StringComparison.Ordinal));

        Assert.EndsWith(": BrandNewStat (2 items), NewDebuff (1 item)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoTableAtAllTheReviewHasNothingToSay()
    {
        Assert.Empty(LeanReview.Lines(Store(withTable: false)));
    }

    [Fact]
    public void TheModelHealthReportCarriesTheReview()
    {
        var store = Golden.LoadStore();

        var lines = ModelHealth.Build(store, ItemScoring.BuildWeightMatrix(store)).Lines();

        Assert.Contains(lines, line => line.Contains("damage type", StringComparison.Ordinal));
    }

    /// <summary>The golden shop, like a patch's: every stat an item has, or debuff it puts on enemies, has a vote in the table, even a vote of 0.</summary>
    [Fact]
    public void EveryStatOfTheGoldenShopHasARowInTheTable()
    {
        var store = Golden.LoadStore();
        var used = store.ItemStats.Values.SelectMany(stats => stats).Select(stat => stat.Stat)
            .Concat(store.ItemEffects.Values.SelectMany(effects => effects).Where(effect => effect.Scope == EffectScope.EnemyDebuff).Select(effect => effect.Effect))
            .ToHashSet();

        Assert.Empty(used.Where(key => !store.StatLeans.ContainsKey(key)).Order());
    }

    [Fact]
    public void TheGoldenTableReadsBulletResistShredderAsABulletItemAndSpiritShredderAsASpiritOne()
    {
        var store = Golden.LoadStore();

        Assert.Equal("spirit", store.Items["bullet_resist_shredder"].Category);
        Assert.True(store.LeanOf("bullet_resist_shredder").Lean > 0, "a spirit item that lowers Bullet Resist leans toward bullet damage");
        Assert.Equal("weapon", store.Items["spirit_shredder_bullets"].Category);
        Assert.True(store.LeanOf("spirit_shredder_bullets").Lean < store.LeanOf("hollow_point").Lean);
    }
}
