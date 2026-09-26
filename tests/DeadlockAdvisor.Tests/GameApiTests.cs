using System.Text.Json.Nodes;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.GameApi;
using DeadlockAdvisor.Tests.Support;
using static DeadlockAdvisor.Tests.Support.Golden;

namespace DeadlockAdvisor.Tests;

/// <summary>The Python app's game sync tests (offline, on fake API records), then parity with its output on a real snapshot.</summary>
public class GameApiTests
{
    private static JsonObject FakeItem(string name, long gameId, int tier, string slot, int cost, params (string Key, JsonObject Prop)[] props)
    {
        var properties = new JsonObject();
        foreach (var (key, prop) in props)
            properties[key] = prop;
        return new JsonObject
        {
            ["id"] = gameId, ["name"] = name, ["item_tier"] = tier, ["item_slot_type"] = slot,
            ["cost"] = cost, ["shopable"] = true, ["properties"] = properties,
        };
    }

    private static (string, JsonObject) Prop(string key, string value, string? section = "innate", params string[] flags) =>
        (key, new JsonObject
        {
            ["value"] = value,
            ["tooltip_section"] = section,
            ["usage_flags"] = new JsonArray(flags.Select(flag => (JsonNode)flag).ToArray()),
        });

    private static Dictionary<(string, bool), double> StatsOf(JsonObject record) =>
        GameSync.ExtractStats("x", record).ToDictionary(s => (s.Stat, s.Conditional), s => s.Value);

    private static Dictionary<string, bool> ConditionalOf(JsonObject record) =>
        GameSync.ExtractStats("x", record).ToDictionary(s => s.Stat, s => s.Conditional);

    [Fact]
    public void ExtractStatsFoldsAliasesAndSplitsConditional()
    {
        var record = FakeItem("Spirit Resilience", 1, 3, "vitality", 3200,
            Prop("TechResist", "30"),
            Prop("TechResistBelowThreshold", "15", "passive", "ConditionallyApplied"),
            Prop("SpiritPower", "4"), Prop("SpiritPowerInnate", "3"),
            Prop("BonusSprintSpeed", "0.75m"),
            Prop("Radius", "12"),
            Prop("BonusHealth", "0"));

        Assert.Equal(new Dictionary<(string, bool), double>
        {
            [("TechResist", false)] = 30.0,
            [("TechResist", true)] = 15.0,
            [("TechPower", false)] = 7.0,
            [("BonusSprintSpeed", false)] = 0.75,
        }, StatsOf(record));
    }

    [Fact]
    public void BuffActiveAndStackedPropertiesFoldIntoTheirStats()
    {
        var colossus = FakeItem("Colossus", 1, 4, "vitality", 6400,
            Prop("BuffBulletResist", "35", "active", "ConditionallyApplied"));
        var vampiricBurst = FakeItem("Vampiric Burst", 2, 4, "weapon", 6400,
            Prop("BulletResist", "10", "innate", "IntrinsicallyProvidedInAbility"),
            Prop("ActiveBonusFireRate", "34", "active"));
        var escalatingResilience = FakeItem("Escalating Resilience", 3, 4, "weapon", 6400,
            Prop("BulletResistPerStack", "2", "passive", "ConditionallyApplied"),
            Prop("MaxArmorStacks", "30", "passive"));
        var shadowWeave = FakeItem("Shadow Weave", 4, 3, "weapon", 3200,
            Prop("AmbushBonusFireRate", "25", "active", "ConditionallyApplied"),
            Prop("AmbushBonusTechPower", "25", "active", "ConditionallyApplied"),
            Prop("AmbushBonusMeleeDamage", "25", "active", "ConditionallyApplied"));

        Assert.Equal(new Dictionary<(string, bool), double> { [("BulletResist", true)] = 35.0 }, StatsOf(colossus));
        Assert.Equal(new Dictionary<(string, bool), double>
        {
            [("BonusFireRate", true)] = 34.0,
            [("BulletResist", false)] = 10.0,
        }, StatsOf(vampiricBurst));
        Assert.Equal(new Dictionary<(string, bool), double> { [("BulletResist", true)] = 30.0 }, StatsOf(escalatingResilience));
        Assert.Equal(new Dictionary<(string, bool), double>
        {
            [("BonusFireRate", true)] = 25.0,
            [("TechPower", true)] = 25.0,
            [("BonusMeleeDamagePercent", true)] = 25.0,
        }, StatsOf(shadowWeave));
    }

    [Fact]
    public void SelfBarriersFoldIntoBarrierButAllyBarriersDoNot()
    {
        var shielding = FakeItem("Weapon Shielding", 1, 2, "vitality", 1600,
            Prop("CombatBarrier", "300", "passive", "ConditionallyApplied"));
        var reactive = FakeItem("Reactive Barrier", 2, 2, "vitality", 1600,
            Prop("VexBarrierCombatBarrier", "325", "passive", "ConditionallyApplied"));
        var ward = FakeItem("Guardian Ward", 3, 2, "vitality", 1600,
            Prop("GuardianWardCombatBarrier", "250", "active"));

        Assert.Equal(new Dictionary<(string, bool), double> { [("Barrier", true)] = 300.0 }, StatsOf(shielding));
        Assert.Equal(new Dictionary<(string, bool), double> { [("Barrier", true)] = 325.0 }, StatsOf(reactive));
        Assert.Empty(StatsOf(ward));
    }

    [Fact]
    public void ConditionallyAppliedInnateStatIsConditional()
    {
        var record = FakeItem("X", 1, 2, "vitality", 1600, Prop("TechResist", "18", "innate", "ConditionallyApplied"));
        Assert.True(GameSync.ExtractStats("x", record)[0].Conditional);
    }

    [Fact]
    public void PlainPassiveStatIsAlwaysOn()
    {
        var record = FakeItem("Debuff Reducer", 1, 2, "vitality", 1600, Prop("StatusResistancePercent", "25", "passive"));
        Assert.Equal(new Dictionary<string, bool> { ["StatusResistancePercent"] = false }, ConditionalOf(record));
    }

    [Fact]
    public void ActiveStatsAreConditionalEvenUnflagged()
    {
        var record = FakeItem("Fury Trance", 1, 3, "vitality", 3200, Prop("TechResist", "40", "active"));
        Assert.Equal(new Dictionary<string, bool> { ["TechResist"] = true }, ConditionalOf(record));
    }

    [Fact]
    public void AFlaggedPassiveMakesItsUnflaggedSiblingsConditional()
    {
        var record = FakeItem("Active Reload", 1, 2, "weapon", 1600,
            Prop("BonusFireRate", "25", "passive", "ConditionallyApplied"),
            Prop("BonusMoveSpeed", "0.75m", "passive"));
        Assert.Equal(new Dictionary<string, bool> { ["BonusFireRate"] = true, ["BonusMoveSpeed"] = true }, ConditionalOf(record));
    }

    [Fact]
    public void PassiveProvidedInAbilityIsConditional()
    {
        var record = FakeItem("Enchanter's Emblem", 1, 2, "vitality", 1600, Prop("TechPower", "15", "passive", "IntrinsicallyProvidedInAbility"));
        Assert.Equal(new Dictionary<string, bool> { ["TechPower"] = true }, ConditionalOf(record));
    }

    [Fact]
    public void ForcedConditionalOverridesTheFlags()
    {
        var record = FakeItem("Mercurial Magnum", 1, 4, "spirit", 6400, Prop("BonusFireRate", "22", "passive"));
        Assert.Equal(new Dictionary<string, bool> { ["BonusFireRate"] = true }, ConditionalOf(record));
    }

    [Fact]
    public void StatsTheTooltipNeverShowsAreIgnored()
    {
        var record = FakeItem("Refresher", 1, 4, "spirit", 6400,
            Prop("TechResist", "14", null), Prop("BulletResist", "15", null), Prop("CooldownReduction", "10", "active"));
        record["tooltip_sections"] = JsonNode.Parse("""
            [{"section_type": "active", "section_attributes": [{"loc_string": "Reset the cooldown",
              "properties": ["AbilityCooldown", "CooldownReduction"]}]}]
            """);
        Assert.Equal(new Dictionary<string, bool> { ["CooldownReduction"] = true }, ConditionalOf(record));
    }

    [Fact]
    public void ForceShownKeepsARealEffectTheTooltipOmits()
    {
        var record = FakeItem("Crippling Headshot", 1, 4, "weapon", 6400,
            Prop("HealAmpReceivePenaltyPercent", "-35", null, "ConditionallyApplied"),
            Prop("TechResist", "5", null));
        record["tooltip_sections"] = JsonNode.Parse("""[{"section_type": "passive", "section_attributes": []}]""");
        var stat = Assert.Single(GameSync.ExtractStats("x", record));
        Assert.Equal(("HealAmpReceivePenaltyPercent", 35.0, true), (stat.Stat, stat.Value, stat.Conditional));
    }

    [Fact]
    public void SelfInflictedPenaltiesAreIgnored()
    {
        var record = FakeItem("Cheat Death", 1, 4, "vitality", 6400,
            Prop("HealAmpReceivePenaltyPercent", "-60", "passive", "ConditionallyApplied"),
            Prop("BonusHealth", "200"));
        Assert.Equal(["BonusHealth"], GameSync.ExtractStats("x", record).Select(s => s.Stat));
    }

    [Fact]
    public void HealingReductionIsStoredPositive()
    {
        var record = FakeItem("Healbane", 1, 2, "vitality", 1600,
            Prop("HealAmpReceivePenaltyPercent", "-35", "passive", "ConditionallyApplied"));
        var stat = Assert.Single(GameSync.ExtractStats("x", record));
        Assert.Equal(35.0, stat.Value);
        Assert.Equal("Healing Reduction", stat.Label);
    }

    private static (string, JsonObject) Labelled(string key, string value, string label) =>
        (key, new JsonObject { ["value"] = value, ["label"] = label, ["tooltip_section"] = "passive" });

    [Fact]
    public void UnmappedStatsFlagsUnknownPropertiesUnderAScoredLabel()
    {
        var record = FakeItem("Long Range", 1, 2, "weapon", 1600,
            Labelled("RenamedWeaponPower", "40", "Weapon Damage"),
            Labelled("BaseAttackDamagePercent", "10", "Weapon Damage"),
            Labelled("SlowPercent", "24", "Move Speed"),
            Labelled("AbilityDuration", "3", "Duration"),
            Labelled("OtherWeaponPower", "0", "Weapon Damage"));

        Assert.Equal(["RenamedWeaponPower (\"Weapon Damage\"): Long Range"], GameSync.UnmappedStats([record]));
    }

    [Fact]
    public void StaleOverridesAreReported()
    {
        var magnum = FakeItem("Mercurial Magnum", 1, 4, "spirit", 6400, Prop("BonusFireRate", "22", "passive", "ConditionallyApplied"));
        var quicksilver = FakeItem("Quicksilver Reload", 2, 2, "spirit", 1600, Prop("TechPower", "10"));
        var crippling = FakeItem("Crippling Headshot", 3, 4, "weapon", 6400, Prop("HealAmpReceivePenaltyPercent", "-35", null));
        crippling["tooltip_sections"] = JsonNode.Parse("""[{"section_type": "passive", "section_attributes": []}]""");

        Assert.Equal(
        [
            "Cheat Death / HealAmpReceivePenaltyPercent: marked self-inflicted, but the item no longer has that property",
            "Mercurial Magnum / BonusFireRate: forced conditional, but the game now flags it conditional itself",
            "Quicksilver Reload / BonusFireRate: forced conditional, but the item no longer has that property",
        ], GameSync.StaleOverrides([magnum, quicksilver, crippling]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ResyncReportsMovedStatsAndChangedTooltipsOnHandRuledItems()
    {
        JsonObject Trinket(string resist, string text)
        {
            var record = FakeItem("Spirit Resist Trinket", 101, 1, "vitality", 800,
                Labelled("TechResist", resist, "Spirit Resist"), Labelled("BonusFireRate", "5", "Fire Rate"));
            record["tooltip_sections"] = new JsonArray(new JsonObject
            {
                ["section_type"] = "innate",
                ["section_attributes"] = new JsonArray(new JsonObject
                {
                    ["loc_string"] = text,
                    ["properties"] = new JsonArray("TechResist", "BonusFireRate"),
                }),
            });
            return record;
        }
        var other = FakeItem("Irrelevant Item", 103, 1, "weapon", 800, Prop("BonusFireRate", "9"));
        var store = TestStore.Make();
        GameSync.Apply(store, [], [Trinket("8", "Resists spirit."), other]);

        var report = GameSync.Apply(store, [], [Trinket("10", "Resists more spirit."), FakeItem("Irrelevant Item", 103, 1, "weapon", 800)]);

        Assert.Equal(
        [
            "Spirit Resist Trinket: Spirit Resist 8% -> 10%",
            "Irrelevant Item: Fire Rate 9% -> none",
        ], report.StatChanges);
        Assert.Equal(["Spirit Resist Trinket"], report.ReviewRules);
    }

    [Fact]
    public void TheSnapshotShopHasNoUnmappedStatsOrStaleOverrides()
    {
        var items = Json("game_api/shop_items.json").AsArray().OfType<JsonNode>()
            .Where(record => GameSync.ShopTiers.Contains(record["item_tier"]!.GetValue<long>()))
            .ToList();
        Assert.Empty(GameSync.UnmappedStats(items));
        Assert.Empty(GameSync.StaleOverrides(items));
    }

    [Fact]
    public void ApplyGameDataFillsIdsAddsItemsAndIsIdempotent()
    {
        var store = TestStore.Make();
        var heroes = JsonNode.Parse("""[{"id": 13, "name": "Heavy Spirit"}, {"id": 2, "name": "Low HP"}, {"id": 3, "name": "Generic"}]""")!.AsArray();
        var items = new JsonArray(
            FakeItem("Spirit Resist Trinket", 101, 1, "vitality", 800, Prop("TechResist", "8")),
            FakeItem("Percent Damage Item", 102, 4, "spirit", 6400),
            FakeItem("Brand New Item", 104, 2, "weapon", 1600, Prop("BonusFireRate", "9")));

        var report = GameSync.Apply(store, heroes, items);
        Assert.Equal(13, store.Heroes["heavy_spirit"].GameId);
        Assert.Equal(101, store.Items["spirit_resist_t1"].GameId);
        Assert.Equal(800, store.Items["spirit_resist_t1"].Cost);
        Assert.Equal(4, store.Items["pct_dmg_t3"].Tier);
        Assert.Contains(report.Changed, line => line.Contains("tier 3 -> 4"));
        Assert.Equal("Brand New Item", store.Items["brand_new_item"].ItemName);
        Assert.Equal(["Brand New Item (T2)"], report.AddedItems);
        Assert.Equal(["Irrelevant Item"], report.NotInGame);
        Assert.Equal(4, report.Filled);
        Assert.Equal(8.0, store.ItemStats["spirit_resist_t1"][0].Value);
        Assert.True(report.AnythingChanged);

        var again = GameSync.Apply(store, heroes, items);
        Assert.False(again.AnythingChanged);
        Assert.Empty(again.AddedItems);
        Assert.Empty(again.Changed);
    }

    [Fact]
    public void TooltipTextKeepsEmphasisAndDropsIcons()
    {
        const string source = "Deals bonus <svg width=\"1\"><path d=\"M0 0\"/></svg>"
                              + "<span class=\"inline-attribute-label Spirit\" style=\"color: #CE90FF;\">Spirit</span> "
                              + "and <span class=\"highlight\">Slows</span>.<br>"
                              + "<span class=\"diminish\">Can't crit &amp; can't stack.</span>";
        Assert.Equal("Deals bonus <span style=\"color:#CE90FF\">Spirit</span> and <b>Slows</b>.<br>"
                     + "<i>Can't crit &amp; can't stack.</i>", TooltipText.From(source));
    }

    [Fact]
    public void ExtractTooltipFormatsValuesAndResolvesComponents()
    {
        var record = FakeItem("Warp Stone", 1, 3, "vitality", 3200);
        record["component_items"] = new JsonArray("upgrade_base", "upgrade_not_ours");
        record["properties"] = JsonNode.Parse("""
            {
              "BonusHealth": {"value": "210", "label": "Bonus Health", "prefix": "{s:sign}"},
              "BonusMoveSpeed": {"value": "-0.5m", "label": "Move Speed", "prefix": "{s:sign}", "postfix": " m", "negative_attribute": true},
              "SlowPercent": {"value": "24", "label": "Move Speed", "prefix": "-", "postfix": "%", "usage_flags": ["ConditionallyApplied"]},
              "AbilityCooldown": {"value": 16.0, "label": "Cooldown", "postfix": "s"},
              "AbilityDuration": {"value": "0", "label": "Duration", "postfix": "s"},
              "HealthThreshold": {"value": "50"}
            }
            """);
        record["tooltip_sections"] = JsonNode.Parse("""
            [
              {"section_type": "innate", "section_attributes": [{"elevated_properties": ["BonusHealth"], "properties": ["BonusMoveSpeed"]}]},
              {"section_attributes": [{
                "loc_string": "Gain a <span class=\"highlight\">Silence</span>.",
                "important_properties": ["StatusEffectEMP", "SlowPercent"],
                "important_properties_with_icon": [{"name": "StatusEffectEMP", "localized_name": "Silenced"}],
                "properties": ["AbilityCooldown", "AbilityDuration", "HealthThreshold"]
              }]}
            ]
            """);

        var tooltip = GameSync.ExtractTooltip("warp", record, new Dictionary<string, string> { ["upgrade_base"] = "base_item" })!;
        var (innate, passive) = (tooltip.Sections[0], tooltip.Sections[1]);
        Assert.Equal("innate", innate.Kind);
        Assert.Equal("+210", innate.Blocks[0].Elevated[0].Value);
        var move = innate.Blocks[0].Stats[0];
        Assert.Equal(("-0.5 m", true), (move.Value, move.Negative));
        Assert.Equal("passive", passive.Kind);
        Assert.Equal("16s", passive.Cooldown);
        var block = passive.Blocks[0];
        Assert.Equal("Gain a <b>Silence</b>.", block.Text);
        Assert.Equal([("", "Silenced", false), ("-24%", "Move Speed", true)], block.Important.Select(s => (s.Value, s.Label, s.Conditional)));
        Assert.Empty(block.Stats);
        Assert.Equal(["base_item"], tooltip.Components);
    }

    [Fact]
    public void MakeIdMatchesExistingConventions()
    {
        Assert.Equal("diviners_kevlar", GameSync.MakeId("Diviner's Kevlar"));
        Assert.Equal("high_velocity_rounds", GameSync.MakeId("High-Velocity Rounds"));
        Assert.Equal("mo_and_krill", GameSync.MakeId("Mo & Krill"));
    }

    // -- parity with the Python sync ------------------------------------------------------

    [Fact]
    public void TooltipTextMatchesPythonForEveryDescriptionAndTheEdgeCases()
    {
        foreach (var pair in Items(Json("game_api/tooltip_text.json")))
            Assert.Equal(Text(pair[1]), TooltipText.From(Text(pair[0])));
    }

    [Fact]
    public void NamesAndNumbersMatchPython()
    {
        var misc = Json("game_api/misc.json");
        foreach (var pair in Items(misc["norm"]))
            Assert.Equal(Text(pair[1]), GameSync.Norm(Text(pair[0])));
        foreach (var pair in Items(misc["make_id"]))
            Assert.Equal(Text(pair[1]), GameSync.MakeId(Text(pair[0])));
        foreach (var pair in Items(misc["number"]))
            Assert.Equal(NullableNumber(pair[1]), GameSync.Number(pair[0]));
    }

    [Theory]
    [InlineData("fresh")]
    [InlineData("stale")]
    public void SyncingTheSnapshotWritesWhatPythonWrites(string name)
    {
        var cases = Json("game_api/sync_cases.json");
        var expected = cases["cases"]![name]!;
        var heroes = Json("game_api/heroes.json").AsArray();
        var items = Json("game_api/shop_items.json").AsArray();
        using var data = CopyData();
        var store = DataStore.Load(data.Path);
        if (name == "stale")
        {
            ApplyStaleOps(store, cases["stale_ops"]!);
            SaveEverything(store);
        }

        var report = GameSync.Apply(store, heroes, items);
        GameSync.SaveSynced(store, report);
        string[] files = ["heroes.csv", "items.csv", "item_stats.csv", "item_tooltips.json", "hero_category_scores.csv"];

        if (Updating)
        {
            WriteReport(expected["report"]!, report);
            foreach (var file in files)
                CopyFile(data.File(file), "game_api", name, file);
            WriteReport(expected["again"]!, GameSync.Apply(store, heroes, items));
            expected["coverage"]!["derived_rules"] = store.Coverage().DerivedRules;
            expected["stat_rules"] = store.StatRules.Count;
            WriteJson("game_api/sync_cases.json", cases);
            return;
        }

        AssertReport(expected["report"]!, report);
        foreach (var file in files)
            AssertEx.BytesEqual(PathOf("game_api", name, file), data.File(file));

        AssertReport(expected["again"]!, GameSync.Apply(store, heroes, items));
        var coverage = store.Coverage();
        Assert.Equal(expected["coverage"]!["derived_rules"]!.GetValue<int>(), coverage.DerivedRules);
        Assert.Equal(expected["stat_rules"]!.GetValue<int>(), store.StatRules.Count);
    }

    private static void WriteReport(JsonNode expected, SyncReport actual)
    {
        expected["lines"] = new JsonArray(actual.Lines().Select(line => (JsonNode)line).ToArray());
        expected["filled"] = actual.Filled;
        expected["stat_rows"] = actual.StatRows;
        expected["tooltip_count"] = actual.TooltipCount;
        expected["heroes_changed"] = actual.HeroesChanged;
        expected["items_changed"] = actual.ItemsChanged;
        expected["stats_changed"] = actual.StatsChanged;
        expected["tooltips_changed"] = actual.TooltipsChanged;
        expected["anything_changed"] = actual.AnythingChanged;
    }

    private static void AssertReport(JsonNode expected, SyncReport actual)
    {
        Assert.Equal(expected["lines"]!.AsArray().Select(Text), actual.Lines());
        Assert.Equal(expected["filled"]!.GetValue<int>(), actual.Filled);
        Assert.Equal(expected["stat_rows"]!.GetValue<int>(), actual.StatRows);
        Assert.Equal(expected["tooltip_count"]!.GetValue<int>(), actual.TooltipCount);
        Assert.Equal(expected["heroes_changed"]!.GetValue<bool>(), actual.HeroesChanged);
        Assert.Equal(expected["items_changed"]!.GetValue<bool>(), actual.ItemsChanged);
        Assert.Equal(expected["stats_changed"]!.GetValue<bool>(), actual.StatsChanged);
        Assert.Equal(expected["tooltips_changed"]!.GetValue<bool>(), actual.TooltipsChanged);
    }

    /// <summary>export_golden.py's apply_stale_ops.</summary>
    private static void ApplyStaleOps(DataStore store, JsonNode ops)
    {
        foreach (var itemId in ops["drop_items"]!.AsArray().Select(Text))
            store.Items.Remove(itemId);
        foreach (var heroId in ops["drop_heroes"]!.AsArray().Select(Text))
            store.Heroes.Remove(heroId);
        foreach (var key in ops["clear_game_ids"]!.AsArray().Select(Text))
        {
            if (store.Heroes.TryGetValue(key, out var hero))
                store.Heroes[key] = hero with { GameId = 0 };
            if (store.Items.TryGetValue(key, out var item))
                store.Items[key] = item with { GameId = 0 };
        }
        foreach (var pair in Items(ops["set_tier"]))
            store.Items[Text(pair[0])] = store.Items[Text(pair[0])] with { Tier = pair[1]!.GetValue<int>() };
        foreach (var pair in Items(ops["set_cost"]))
            store.Items[Text(pair[0])] = store.Items[Text(pair[0])] with { Cost = pair[1]!.GetValue<int>() };
        if (ops["clear_stats"]!.GetValue<bool>())
        {
            store.ItemStats = [];
            store.RebuildDerived();
        }
        if (ops["clear_tooltips"]!.GetValue<bool>())
            store.ItemTooltips = [];
    }

    /// <summary>export_golden.py's save_everything.</summary>
    private static void SaveEverything(DataStore store)
    {
        store.SaveHeroes();
        store.SaveItems();
        store.SaveHeroScores();
        store.SaveItemCoefficients();
        store.SaveTraitWeights();
        store.SaveStatRules();
        store.SaveItemStats();
        store.SaveItemTooltips();
        store.SaveMatchLift();
    }
}
