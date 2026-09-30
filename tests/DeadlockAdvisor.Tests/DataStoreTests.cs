using System.Text.Json.Nodes;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Formats;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

/// <summary>Ported from the Python app's tests/test_scoring.py (the data layer's editing helpers).</summary>
public class DataStoreTests
{
    [Fact]
    public void SetCoefficientZeroRemovesTheRule()
    {
        var store = TestStore.Make();
        Assert.True(store.SetCoefficient("spirit_resist_t1", "max_hp", Relation.With, 1.5));
        Assert.Equal(1.5, store.Coefficient("spirit_resist_t1", "max_hp", Relation.With));
        // setting the same value again is a no-op, so callers can skip a resave
        Assert.False(store.SetCoefficient("spirit_resist_t1", "max_hp", Relation.With, 1.5));
        // zero deletes rather than storing a zero row: the table is sparse
        Assert.True(store.SetCoefficient("spirit_resist_t1", "max_hp", Relation.With, 0));
        Assert.False(store.ItemCoefficients.ContainsKey(new CoefficientKey("spirit_resist_t1", "max_hp", Relation.With)));
    }

    [Fact]
    public void CopyAndClearHeroScores()
    {
        var store = TestStore.Make();
        Assert.True(store.CopyHeroScores("heavy_spirit", "generic"));
        Assert.Equal(5, store.HeroScore("generic", "deals_spirit_damage_general"));
        Assert.True(store.ClearHeroScores("generic"));
        Assert.Equal(0, store.HeroFilledCount("generic"));
    }

    [Fact]
    public void CopyItemRulesReplacesTheTarget()
    {
        var store = TestStore.Make();
        store.CopyItemRules("spirit_resist_t1", "irrelevant_t1");

        Assert.Equal([("deals_spirit_damage_general", Relation.Against, 2.0)], store.RulesForItem("irrelevant_t1"));
        Assert.Equal(1, store.RuleCount("irrelevant_t1"));
    }

    [Fact]
    public void CoverageAndUncoveredItems()
    {
        var store = TestStore.Make();
        var coverage = store.Coverage();
        Assert.Equal(3, coverage.ItemsTotal);
        Assert.Equal(2, coverage.ItemsTagged); // irrelevant_t1 has no rules
        Assert.Equal(["irrelevant_t1"], store.UncoveredItems());
        Assert.Empty(store.UnprofiledHeroes()); // every hero has at least one nonzero trait
    }

    [Fact]
    public void PruneOrphansDropsRowsForDeletedIds()
    {
        var store = TestStore.Make();
        store.HeroScores[new ScoreKey("ghost_hero", "max_hp")] = 3;
        store.ItemCoefficients[new CoefficientKey("ghost_item", "max_hp", Relation.Against)] = 1;

        Assert.Equal(2, store.PruneOrphans());
        Assert.False(store.HeroScores.ContainsKey(new ScoreKey("ghost_hero", "max_hp")));
        Assert.False(store.ItemCoefficients.ContainsKey(new CoefficientKey("ghost_item", "max_hp", Relation.Against)));
    }

    [Fact]
    public void PruneOrphansDropsWeightsForDeletedTraits()
    {
        var store = TestStore.Make();
        store.TraitWeights[new WeightKey("ghost_trait", Relation.Against)] = 2.0;

        Assert.Equal(1, store.PruneOrphans());
        Assert.Empty(store.TraitWeights);
    }

    /// <summary>Saving and reloading must give back exactly what was in memory: the app autosaves on every keystroke.</summary>
    [Fact]
    public void CsvRoundTripPreservesValues()
    {
        var store = TestStore.Make();
        using var temp = new TempDirectory();
        store.DataDir = temp.Path;
        // the loaders need these to exist as well, in the old two/four-column shapes
        File.WriteAllText(temp.File("heroes.csv"),
            "hero_id,hero_name\n" + string.Concat(store.Heroes.Values.Select(h => $"{h.HeroId},{h.HeroName}\n")));
        File.WriteAllText(temp.File("items.csv"),
            "item_id,item_name,category,tier\n" + string.Concat(store.Items.Values.Select(i => $"{i.ItemId},{i.ItemName},{i.Category},{i.Tier}\n")));
        File.WriteAllText(temp.File("categories.csv"),
            "category_id,category_name,scale_min,scale_max,description\n"
            + "deals_spirit_damage_general,Deals Spirit Damage,0,5,\nmax_hp,Has High Max HP,-5,5,\n");
        store.SetHeroScore("generic", "max_hp", -2.5);
        store.SaveAll();

        var reloaded = DataStore.Load(temp.Path);

        Assert.Equal(-2.5, reloaded.HeroScore("generic", "max_hp"));
        Assert.Equal(5, reloaded.HeroScore("heavy_spirit", "deals_spirit_damage_general"));
        Assert.Equal(store.ItemCoefficients, reloaded.ItemCoefficients);
        Assert.Equal(0, reloaded.Heroes["generic"].GameId);
        Assert.Equal(0, reloaded.Items["spirit_resist_t1"].Cost);
        // no trait_weights / stat_rules / item_stats files is fine too
        Assert.Empty(reloaded.TraitWeights);
        Assert.Empty(reloaded.Derived);
    }

    [Fact]
    public void ConditionalStatsCountAtTheConditionalFactor()
    {
        var store = TestStore.WithStats(TestStore.Make());

        var amounts = store.DerivedParts("spirit_resist_t1", "deals_spirit_damage_general", Relation.Against)
            .Select(p => p.Amount).Order().ToList();

        Assert.Equal([0.5, 2.0], amounts); // 4 * 0.25 * 0.5 conditional, 8 * 0.25 always-on
    }

    [Fact]
    public void StatDerivedRulesCountAsCoverage()
    {
        var store = TestStore.WithStats(TestStore.Make());

        Assert.Empty(store.UncoveredItems()); // irrelevant_t1 is covered by its stats now
        Assert.Equal(0, store.RuleCount("irrelevant_t1"));
        Assert.Equal(1, store.DerivedRuleCount("irrelevant_t1"));
        Assert.Equal(2, store.Coverage().DerivedRules);
    }

    [Fact]
    public void StatRulesForUnknownCategoriesAreIgnored()
    {
        var store = TestStore.WithStats(TestStore.Make());
        store.StatRules[new StatRuleKey("TechResist", "ghost_trait", Relation.Against)] =
            new StatRule("TechResist", "ghost_trait", Relation.Against, 1.0);

        store.RebuildDerived();

        Assert.All(store.Derived.Keys, key => Assert.NotEqual("ghost_trait", key.CategoryId));
    }

    [Fact]
    public void NewCsvsRoundTrip()
    {
        var store = TestStore.WithStats(TestStore.Make());
        store.Items["spirit_resist_t1"] = new Item("spirit_resist_t1", "Spirit Resist Trinket", "vitality", 1, 42, 800);
        store.Heroes["generic"] = new Hero("generic", "Generic", 7);
        store.SetTraitWeight("max_hp", Relation.Against, 0.75);
        using var temp = new TempDirectory();
        store.DataDir = temp.Path;
        File.WriteAllText(temp.File("categories.csv"),
            "category_id,category_name,scale_min,scale_max,description\n"
            + "deals_spirit_damage_general,Deals Spirit Damage,0,5,\nmax_hp,Has High Max HP,-5,5,\n");
        File.WriteAllText(temp.File("stat_rules.csv"),
            "stat,category_id,relation,per_unit,conditional_factor,note\n"
            + "TechResist,deals_spirit_damage_general,against,0.25,,blank factor = no discount\n");
        store.SaveHeroes();
        store.SaveItems();
        store.SaveItemStats();
        store.SaveAll();

        var reloaded = DataStore.Load(temp.Path);

        Assert.Equal(42, reloaded.Items["spirit_resist_t1"].GameId);
        Assert.Equal(800, reloaded.Items["spirit_resist_t1"].Cost);
        Assert.Equal(7, reloaded.Heroes["generic"].GameId);
        Assert.Equal(0.75, reloaded.TraitWeight("max_hp", Relation.Against));
        // Saved in items.csv order; Python's dict == ignores order too.
        Assert.Equal(store.ItemStats.Keys.ToHashSet(), reloaded.ItemStats.Keys.ToHashSet());
        foreach (var (itemId, stats) in store.ItemStats)
            Assert.Equal(stats, reloaded.ItemStats[itemId]);
        var rule = reloaded.StatRules[new StatRuleKey("TechResist", "deals_spirit_damage_general", Relation.Against)];
        Assert.Equal(1.0, rule.ConditionalFactor);
        Assert.Equal("blank factor = no discount", rule.Note);
        // 8 * 0.25 + 4 * 0.25 * 1.0
        Assert.Equal(3.0, reloaded.DerivedCoefficient("spirit_resist_t1", "deals_spirit_damage_general", Relation.Against));
    }

    [Fact]
    public void StatCatalogListsStatsItemsActuallyHave()
    {
        var store = TestStore.WithStats(TestStore.Make());

        // spirit_resist_t1 carries TechResist twice (always-on + conditional): still one item
        Assert.Equal([new("TechResist", new StatCatalogEntry("Spirit Resist", "%", 2))], store.StatCatalog());
    }

    [Fact]
    public void SetStatRuleAddsEditsAndRebuilds()
    {
        var store = TestStore.WithStats(TestStore.Make());
        var rule = new StatRule("TechResist", "max_hp", Relation.With, 0.5, 0.5);

        Assert.True(store.SetStatRule(rule));
        Assert.False(store.SetStatRule(rule)); // unchanged -> no save needed
        Assert.Equal([rule], store.StatRulesFor("max_hp", Relation.With));
        Assert.Equal(10.0, store.DerivedCoefficient("irrelevant_t1", "max_hp", Relation.With)); // 20 * 0.5

        store.SetStatRule(rule with { PerUnit = 0.25 });
        Assert.Equal(5.0, store.DerivedCoefficient("irrelevant_t1", "max_hp", Relation.With));

        Assert.True(store.RemoveStatRule("TechResist", "max_hp", Relation.With));
        Assert.Equal(0, store.DerivedCoefficient("irrelevant_t1", "max_hp", Relation.With));
        Assert.False(store.RemoveStatRule("TechResist", "max_hp", Relation.With));
    }

    [Fact]
    public void RetargetingAStatRuleKeepsItsPlaceAndRefusesDuplicates()
    {
        var store = TestStore.WithStats(TestStore.Make());
        var first = new StatRule("BulletResist", "deals_spirit_damage_general", Relation.Against, 1.0);
        store.SetStatRule(first);
        var orderBefore = store.StatRules.Keys.ToList();
        // moving the TechResist rule onto BulletResist collides with `first`
        var moved = new StatRule("BulletResist", "deals_spirit_damage_general", Relation.Against, 0.25, 0.5);

        Assert.False(store.SetStatRule(moved, replacing: "TechResist"));
        Assert.Equal(orderBefore, store.StatRules.Keys);

        store.RemoveStatRule("BulletResist", "deals_spirit_damage_general", Relation.Against);
        Assert.True(store.SetStatRule(moved, replacing: "TechResist"));
        Assert.Equal([new StatRuleKey("BulletResist", "deals_spirit_damage_general", Relation.Against)], store.StatRules.Keys);
        Assert.Empty(store.Derived); // no item has bullet resist
    }

    [Fact]
    public void RetargetingKeepsTheRuleInPlaceAmongOthers()
    {
        var store = TestStore.WithStats(TestStore.Make());
        store.SetStatRule(new StatRule("BonusHealth", "deals_spirit_damage_general", Relation.Against, 1.0));
        store.SetStatRule(new StatRule("TechPower", "deals_spirit_damage_general", Relation.Against, 1.0));

        store.SetStatRule(new StatRule("BulletResist", "deals_spirit_damage_general", Relation.Against, 2.0), replacing: "BonusHealth");

        Assert.Equal(["TechResist", "BulletResist", "TechPower"], store.StatRules.Keys.Select(k => k.Stat));
    }

    [Fact]
    public void SuggestPerUnitAimsATypicalItemAtMild()
    {
        var store = TestStore.WithStats(TestStore.Make());

        // always-on TechResist values are 20 and 8 -> median pick 20 -> 2 / 20
        Assert.Equal(0.1, store.SuggestPerUnit("TechResist"));
        Assert.Equal(0.1, store.SuggestPerUnit("NoSuchStat"));
    }

    [Fact]
    public void StatRulesRoundTripThroughCsv()
    {
        var store = TestStore.WithStats(TestStore.Make());
        store.StatRules[new StatRuleKey("TechResist", "deals_spirit_damage_general", Relation.Against)] =
            new StatRule("TechResist", "deals_spirit_damage_general", Relation.Against, 0.25, 0.5, "has \"quotes\", commas");
        using var temp = new TempDirectory();
        store.DataDir = temp.Path;
        store.SaveStatRules();

        var reloaded = new DataStore(temp.Path);
        reloaded.LoadStatRules();

        Assert.Equal(store.StatRules, reloaded.StatRules);
    }

    [Fact]
    public void ItemTooltipsRoundTripAndInvertComponents()
    {
        var store = TestStore.Make();
        store.ItemTooltips = new()
        {
            ["pct_dmg_t3"] = new ItemTooltip("pct_dmg_t3",
                new[]
                {
                    new TooltipSection("active", "16s", new[]
                    {
                        new TooltipBlock("<b>Go</b>", EquatableList<TooltipStat>.Empty,
                            new[] { new TooltipStat("11m", "Range", true), new TooltipStat("2.3%", "Max Health per second", SpiritScale: 0.0055) }.ToEquatableList(),
                            EquatableList<TooltipStat>.Empty),
                    }.ToEquatableList()),
                }.ToEquatableList(),
                new[] { "spirit_resist_t1" }.ToEquatableList()),
        };
        using var temp = new TempDirectory();
        store.DataDir = temp.Path;
        store.SaveItemTooltips();

        var loaded = new DataStore(temp.Path);
        loaded.LoadItemTooltips();

        Assert.Equal(store.ItemTooltips, loaded.ItemTooltips);
        Assert.Equal(["pct_dmg_t3"], store.UpgradesTo("spirit_resist_t1"));
        Assert.Empty(store.UpgradesTo("pct_dmg_t3"));
    }

    [Fact]
    public void MatchLiftRoundTripsAndIsOptional()
    {
        var store = TestStore.Make();
        TestStore.AddLifts(store);
        store.MatchMeta = new JsonObject
        {
            ["fetched_at"] = 1790000000,
            ["families"] = new JsonObject { ["against/full"] = new JsonObject { ["kept"] = true } },
        };
        using var temp = new TempDirectory();
        var empty = new DataStore(temp.Path);
        empty.LoadMatchLift(); // no files: loads nothing, raises nothing
        Assert.Empty(empty.MatchLift);
        Assert.Empty(empty.MatchMeta);

        store.DataDir = temp.Path;
        store.SaveMatchLift();
        var loaded = new DataStore(temp.Path);
        loaded.LoadMatchLift();

        Assert.Equal(store.MatchLift, loaded.MatchLift);
        Assert.Equal(PythonJson.ToFileBytes(store.MatchMeta, true), PythonJson.ToFileBytes(loaded.MatchMeta, true));
    }

    [Fact]
    public void SearchTextStripsMarkupAndIncludesStats()
    {
        var tooltip = new ItemTooltip("x",
            new[]
            {
                new TooltipSection("passive", "", new[]
                {
                    new TooltipBlock("Deals <b>bonus</b><br>Spirit &amp; more", EquatableList<TooltipStat>.Empty,
                        new[] { new TooltipStat("+20%", "Max Ammo") }.ToEquatableList(), EquatableList<TooltipStat>.Empty),
                }.ToEquatableList()),
            }.ToEquatableList(),
            EquatableList<string>.Empty);

        Assert.Equal("passive deals bonus spirit & more +20% max ammo", tooltip.SearchText);
    }
}
