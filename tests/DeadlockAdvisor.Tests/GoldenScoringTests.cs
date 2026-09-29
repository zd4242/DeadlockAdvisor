using System.Text.Json.Nodes;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Support;
using static DeadlockAdvisor.Tests.Support.Golden;

namespace DeadlockAdvisor.Tests;

/// <summary>Scoring on the real data must reproduce the Python app's numbers, names and order.</summary>
public class GoldenScoringTests
{
    private static Relation ParseRelation(JsonNode? node) =>
        Relations.TryParse(Text(node), out var relation) ? relation : throw new FormatException(Text(node));

    [Fact]
    public void EffectiveCoefficientsMatch()
    {
        var expected = Items(Json("effective_coefficients.json")).ToDictionary(
            row => new CoefficientKey(Text(row["item_id"]), Text(row["category_id"]), ParseRelation(row["relation"])),
            row => Number(row["value"]));

        var actual = LoadStore().EffectiveCoefficients();

        Assert.Equal(expected.Keys.ToHashSet(), actual.Keys.ToHashSet());
        foreach (var (key, value) in expected)
            AssertEx.Close(value, actual[key], because: key.ToString());
    }

    [Fact]
    public void WeightMatrixMatches()
    {
        var actual = ItemScoring.BuildWeightMatrix(LoadStore());
        if (Updating)
        {
            WriteJson("weight_matrix.json", new JsonArray(actual
                .OrderBy(entry => entry.Key.ItemId, StringComparer.Ordinal)
                .ThenBy(entry => entry.Key.HeroId, StringComparer.Ordinal)
                .ThenBy(entry => entry.Key.Relation.Key(), StringComparer.Ordinal)
                .Select(entry => (JsonNode)new JsonObject
                {
                    ["item_id"] = entry.Key.ItemId,
                    ["hero_id"] = entry.Key.HeroId,
                    ["relation"] = entry.Key.Relation.Key(),
                    ["weight"] = entry.Value,
                })
                .ToArray()));
        }

        var expected = Items(Json("weight_matrix.json")).ToDictionary(
            row => new MatrixKey(Text(row["item_id"]), Text(row["hero_id"]), ParseRelation(row["relation"])),
            row => Number(row["weight"]));

        Assert.Equal(expected.Keys.ToHashSet(), actual.Keys.ToHashSet());
        foreach (var (key, value) in expected)
            AssertEx.Close(value, actual[key], because: key.ToString());
    }

    public static TheoryData<string> Cases() =>
        new(Items(Json("scoring_cases.json")).Select(row => Text(row["name"])));

    [Theory]
    [MemberData(nameof(Cases))]
    public void ScoringCaseMatches(string name)
    {
        var cases = Json("scoring_cases.json");
        var expected = Items(cases).Single(row => Text(row["name"]) == name);
        var store = LoadStore();
        if (expected["drop_match_data"]!.GetValue<bool>())
            store.MatchLift = [];
        var match = Replay(expected["ops"]);
        var matrix = ItemScoring.BuildWeightMatrix(store);

        if (Updating)
        {
            expected["full_match_results"] = Grouped(ItemScoring.FullMatchResults(store, matrix, match));
            foreach (var explained in Items(expected["explain"]))
                explained["contributions"] = Contributions(ItemScoring.ExplainItem(store, match, Text(explained["item_id"])));
            expected["data_only_picks"] = ScoredList(ItemScoring.DataOnlyPicks(store, matrix, match));
            WriteJson("scoring_cases.json", cases);
        }

        Assert.Equal(Strings(expected["allies"]), match.Allies);
        Assert.Equal(Strings(expected["enemies"]), match.Enemies);
        Assert.Equal((string?)expected["self"], match.SelfHero);
        var saved = match.ToSaved();
        Assert.Equal(expected["saved"]!["roles"]!.AsObject().Select(p => (p.Key, Text(p.Value))), saved.Roles.Select(p => (p.Key, p.Value)));

        AssertGrouped(expected["full_match_results"], ItemScoring.FullMatchResults(store, matrix, match));
        foreach (var explained in Items(expected["explain"]))
            AssertContributions(explained["contributions"], ItemScoring.ExplainItem(store, match, Text(explained["item_id"])));
        AssertScoredList(expected["data_only_picks"], ItemScoring.DataOnlyPicks(store, matrix, match));
        foreach (var row in Items(expected["data_scores"]))
            AssertPairs(row[1], ItemScoring.DataScores(store, match, Text(row[0])));
    }

    [Fact]
    public void StoreQueriesMatch()
    {
        var expected = Json("store_queries.json");
        var store = LoadStore();
        if (Updating)
        {
            foreach (var row in Items(expected["item_contributions"]))
                row[2] = Contributions(ItemScoring.ItemContributions(store, Text(row[0]), ParseRelation(row[1])));
            foreach (var row in Items(expected["item_matches"]))
                row[1] = new JsonArray(store.Items.Keys.Where(itemId => store.ItemMatches(itemId, Text(row[0]))).Select(id => (JsonNode)id).ToArray());
            WriteJson("store_queries.json", expected);
        }

        var coverage = expected["coverage"]!;
        Assert.Equal(new Coverage(
            (int)coverage["heroes_total"]!, (int)coverage["heroes_profiled"]!, (int)coverage["items_total"]!,
            (int)coverage["items_tagged"]!, (int)coverage["rules"]!, (int)coverage["derived_rules"]!,
            (int)coverage["scores_filled"]!, (int)coverage["scores_total"]!), store.Coverage());
        Assert.Equal(Strings(expected["uncovered_items"]), store.UncoveredItems());
        Assert.Equal(Strings(expected["unprofiled_heroes"]), store.UnprofiledHeroes());
        Assert.Equal(Strings(expected["heroes_sorted"]), store.HeroesSorted().Select(h => h.HeroId));
        Assert.Equal(Strings(expected["items_sorted"]), store.ItemsSorted().Select(i => i.ItemId));

        foreach (var row in Items(expected["category_short_names"]))
        {
            var category = store.Categories[Text(row[0])];
            Assert.Equal(Text(row[1]), category.ShortName);
            Assert.Equal(row[2]!.GetValue<bool>(), category.IsSigned);
        }
        foreach (var row in Items(expected["hero_filled_count"]))
            Assert.Equal((int)row[1]!, store.HeroFilledCount(Text(row[0])));

        var catalog = store.StatCatalog();
        Assert.Equal(Items(expected["stat_catalog"]).Select(row => Text(row[0])), catalog.Keys);
        foreach (var row in Items(expected["stat_catalog"]))
            Assert.Equal(new StatCatalogEntry(Text(row[1]![0]), Text(row[1]![1]), (int)row[1]![2]!), catalog[Text(row[0])]);
        foreach (var row in Items(expected["suggest_per_unit"]))
            Assert.Equal(Number(row[1]), store.SuggestPerUnit(Text(row[0])));

        foreach (var row in Items(expected["rules_for_item"]))
        {
            var rules = store.RulesForItem(Text(row[0]));
            Assert.Equal(Items(row[1]).Select(r => (Text(r[0]), Text(r[1]), Number(r[2]))),
                rules.Select(r => (r.CategoryId, r.Relation.Key(), r.Coefficient)));
        }
        foreach (var row in Items(expected["rule_count"]))
        {
            Assert.Equal((int)row[1]!, store.RuleCount(Text(row[0])));
            Assert.Equal((int)row[2]!, store.DerivedRuleCount(Text(row[0])));
        }
        foreach (var row in Items(expected["derived_rules_for_item"]))
        {
            var derived = store.DerivedRulesForItem(Text(row[0]));
            var expectedRules = Items(row[1]).ToList();
            Assert.Equal(expectedRules.Count, derived.Count);
            for (var i = 0; i < derived.Count; i++)
            {
                Assert.Equal(Text(expectedRules[i][0]), derived[i].CategoryId);
                Assert.Equal(Text(expectedRules[i][1]), derived[i].Relation.Key());
                Assert.Equal(Strings(expectedRules[i][2]), derived[i].Parts.Select(p => p.Describe()));
                AssertEx.Close(Number(expectedRules[i][3]), DataStore.SumAmounts(derived[i].Parts));
            }
        }
        foreach (var row in Items(expected["stat_part_short"]))
        {
            Assert.Equal(Items(row[1]).Select(Strings), store.DerivedRulesForItem(Text(row[0])).Select(r => r.Parts.Select(p => p.Short()).ToList()));
        }
        foreach (var row in Items(expected["upgrades_to"]))
            Assert.Equal(Strings(row[1]), store.UpgradesTo(Text(row[0])));
        foreach (var row in Items(expected["search_text"]))
            Assert.Equal(Text(row[1]), store.ItemTooltips[Text(row[0])].SearchText);
        foreach (var row in Items(expected["item_matches"]))
        {
            var needle = Text(row[0]);
            Assert.Equal(Strings(row[1]), store.Items.Keys.Where(itemId => store.ItemMatches(itemId, needle)));
        }
        foreach (var row in Items(expected["items_for_category"]))
        {
            var relation = ParseRelation(row[1]);
            AssertPairs(row[2], store.ItemsForCategory(Text(row[0]), relation));
            AssertPairs(row[3], store.DerivedItemsForCategory(Text(row[0]), relation));
        }
        foreach (var row in Items(expected["stat_rules_for"]))
        {
            var rules = store.StatRulesFor(Text(row[0]), ParseRelation(row[1]));
            Assert.Equal(Items(row[2]).Select(r => (Text(r[0]), Number(r[1]), Number(r[2]), Text(r[3]))),
                rules.Select(r => (r.Stat, r.PerUnit, r.ConditionalFactor, r.Note)));
        }
        foreach (var row in Items(expected["item_contributions"]))
            AssertContributions(row[2], ItemScoring.ItemContributions(store, Text(row[0]), ParseRelation(row[1])));

        Assert.Equal(Text(expected["match_summary"]), MatchStatsMath.Summary(store.MatchMeta, 1790400000));
    }

    private static MatchState Replay(JsonNode? ops)
    {
        var match = new MatchState();
        foreach (var op in Items(ops))
            match.SetRole(Text(op[1]), Roles.TryParse(Text(op[2]), out var role) ? role : throw new FormatException());
        return match;
    }

    private static List<string> Strings(JsonNode? node) => Items(node).Select(Text).ToList();

    private static void AssertGrouped(JsonNode? expected, OrderedDictionary<int, List<ScoredItem>> actual)
    {
        var tiers = Items(expected).ToList();
        Assert.Equal(tiers.Select(t => (int)t["tier"]!), actual.Keys);
        foreach (var tier in tiers)
            AssertScoredList(tier["items"], actual[(int)tier["tier"]!]);
    }

    private static void AssertScoredList(JsonNode? expected, IReadOnlyList<ScoredItem> actual)
    {
        var rows = Items(expected).ToList();
        Assert.Equal(rows.Select(r => Text(r["item_id"])), actual.Select(a => a.ItemId));
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(Text(rows[i]["item_name"]), actual[i].ItemName);
            Assert.Equal((int)rows[i]["tier"]!, actual[i].Tier);
            Assert.Equal(Text(rows[i]["shop_category"]), actual[i].ShopCategory);
            AssertEx.Close(Number(rows[i]["score"]), actual[i].Score, because: actual[i].ItemId);
            AssertPairs(rows[i]["data"], actual[i].Data);
        }
    }

    private static void AssertPairs(JsonNode? expected, OrderedDictionary<string, double> actual)
    {
        var pairs = Items(expected).ToList();
        Assert.Equal(pairs.Select(p => Text(p[0])), actual.Keys);
        foreach (var pair in pairs)
            AssertEx.Close(Number(pair[1]), actual[Text(pair[0])], because: Text(pair[0]));
    }

    private static void AssertContributions(JsonNode? expected, IReadOnlyList<HeroContribution> actual)
    {
        var rows = Items(expected).ToList();
        Assert.Equal(rows.Select(r => Text(r["hero_id"])), actual.Select(a => a.HeroId));
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var contribution = actual[i];
            Assert.Equal(Text(row["hero_name"]), contribution.HeroName);
            Assert.Equal(Text(row["relation"]), contribution.Relation.Key());
            AssertEx.Close(Number(row["amount"]), contribution.Amount);

            var parts = Items(row["parts"]).ToList();
            Assert.Equal(parts.Select(p => Text(p["category_id"])), contribution.Parts.Select(p => p.CategoryId));
            for (var j = 0; j < parts.Count; j++)
            {
                var part = contribution.Parts[j];
                Assert.Equal(Text(parts[j]["category_name"]), part.CategoryName);
                AssertEx.Close(Number(parts[j]["hero_score"]), part.HeroScore);
                AssertEx.Close(Number(parts[j]["baseline"]), part.Baseline);
                AssertEx.Close(Number(parts[j]["coefficient"]), part.Coefficient);
                AssertEx.Close(Number(parts[j]["weight"]), part.Weight);
                AssertEx.Close(Number(parts[j]["from_stats"]), part.FromStats);
                AssertEx.Close(Number(parts[j]["effective_coefficient"]), part.EffectiveCoefficient);
                AssertEx.Close(Number(parts[j]["amount"]), part.Amount);
                Assert.Equal(Strings(parts[j]["stat_parts"]), part.StatParts.Select(s => s.Describe()));
            }
        }
    }

    // -- writing goldens back (DEADLOCK_UPDATE_GOLDENS=1), in the shapes the asserts above read ---------

    private static JsonArray Grouped(OrderedDictionary<int, List<ScoredItem>> grouped) =>
        new(grouped.Select(pair => (JsonNode)new JsonObject { ["tier"] = pair.Key, ["items"] = ScoredList(pair.Value) }).ToArray());

    private static JsonArray ScoredList(IEnumerable<ScoredItem> items) =>
        new(items.Select(item => (JsonNode)new JsonObject
        {
            ["item_id"] = item.ItemId,
            ["item_name"] = item.ItemName,
            ["tier"] = item.Tier,
            ["score"] = item.Score,
            ["shop_category"] = item.ShopCategory,
            ["data"] = new JsonArray(item.Data.Select(pair => (JsonNode)new JsonArray(pair.Key, pair.Value)).ToArray()),
        }).ToArray());

    private static JsonArray Contributions(IEnumerable<HeroContribution> contributions) =>
        new(contributions.Select(contribution => (JsonNode)new JsonObject
        {
            ["hero_id"] = contribution.HeroId,
            ["hero_name"] = contribution.HeroName,
            ["relation"] = contribution.Relation.Key(),
            ["amount"] = contribution.Amount,
            ["parts"] = new JsonArray(contribution.Parts.Select(part => (JsonNode)new JsonObject
            {
                ["category_id"] = part.CategoryId,
                ["category_name"] = part.CategoryName,
                ["hero_score"] = part.HeroScore,
                ["baseline"] = part.Baseline,
                ["coefficient"] = part.Coefficient,
                ["weight"] = part.Weight,
                ["from_stats"] = part.FromStats,
                ["effective_coefficient"] = part.EffectiveCoefficient,
                ["amount"] = part.Amount,
                ["stat_parts"] = new JsonArray(part.StatParts.Select(stat => (JsonNode)stat.Describe()).ToArray()),
            }).ToArray()),
        }).ToArray());
}
