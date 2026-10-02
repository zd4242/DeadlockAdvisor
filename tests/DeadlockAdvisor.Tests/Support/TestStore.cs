using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Tests.Support;

/// <summary>
/// A small synthetic dataset: fast, and the expected
/// numbers can be checked by hand.
/// </summary>
public static class TestStore
{
    public static DataStore Make()
    {
        var store = new DataStore(".")
        {
            Heroes = new()
            {
                ["heavy_spirit"] = new Hero("heavy_spirit", "Heavy Spirit"),
                ["low_hp"] = new Hero("low_hp", "Low HP"),
                ["generic"] = new Hero("generic", "Generic"),
            },
            Items = new()
            {
                ["spirit_resist_t1"] = new Item("spirit_resist_t1", "Spirit Resist Trinket", "vitality", 1),
                ["pct_dmg_t3"] = new Item("pct_dmg_t3", "Percent Damage Item", "spirit", 3),
                ["irrelevant_t1"] = new Item("irrelevant_t1", "Irrelevant Item", "weapon", 1),
            },
            Categories = new()
            {
                ["deals_spirit_damage_general"] = new Category("deals_spirit_damage_general", "Deals Spirit Damage", 0, 5, ""),
                ["max_hp"] = new Category("max_hp", "Has High Max HP", -5, 5, ""),
            },
            HeroScores = new()
            {
                [new ScoreKey("heavy_spirit", "deals_spirit_damage_general")] = 5,
                [new ScoreKey("heavy_spirit", "max_hp")] = 0,
                [new ScoreKey("low_hp", "deals_spirit_damage_general")] = 0,
                [new ScoreKey("low_hp", "max_hp")] = -4,
                [new ScoreKey("generic", "deals_spirit_damage_general")] = 1,
                [new ScoreKey("generic", "max_hp")] = 0,
            },
            // Spirit resist counters spirit damage on an enemy; the percent-damage item scales with
            // (and is discouraged by low) max HP, also "against".
            ItemCoefficients = new()
            {
                [new CoefficientKey("spirit_resist_t1", "deals_spirit_damage_general", Relation.Against)] = 2.0,
                [new CoefficientKey("pct_dmg_t3", "max_hp", Relation.Against)] = 1.0,
            },
        };
        return store;
    }

    /// <summary>
    /// Spirit resist on two items, one always-on and one conditional, and a rule turning each point
    /// of it into 0.25 against spirit damage.
    /// </summary>
    public static DataStore WithStats(DataStore store)
    {
        store.ItemStats = new()
        {
            ["irrelevant_t1"] = [new ItemStat("irrelevant_t1", "TechResist", "Spirit Resist", 20, "%", false)],
            ["spirit_resist_t1"] =
            [
                new ItemStat("spirit_resist_t1", "TechResist", "Spirit Resist", 8, "%", false),
                new ItemStat("spirit_resist_t1", "TechResist", "Spirit Resist", 4, "%", true),
            ],
        };
        store.StatRules = new()
        {
            [new StatRuleKey("TechResist", "deals_spirit_damage_general", Relation.Against)] =
                new StatRule("TechResist", "deals_spirit_damage_general", Relation.Against, 0.25, 0.5),
        };
        store.RebuildDerived();
        return store;
    }

    public static void AddLifts(DataStore store)
    {
        (string Item, string Hero, string Relation, double Shrunk)[] lifts =
        [
            ("spirit_resist_t1", "heavy_spirit", "against", 1.5),
            ("spirit_resist_t1", "generic", "against", -0.25),
            ("spirit_resist_t1", "low_hp", "as", 3.0),
            ("irrelevant_t1", "heavy_spirit", "against", 1.2),
        ];
        foreach (var (item, hero, relation, shrunk) in lifts)
            store.MatchLift[new MatchLiftKey(item, hero, relation)] = new MatchLift(item, hero, relation, 5000, shrunk + 0.1, 0.5, shrunk);
    }
}
