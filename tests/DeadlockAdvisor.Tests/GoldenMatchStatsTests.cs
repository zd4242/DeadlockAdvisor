using System.Text;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Formats;
using DeadlockAdvisor.Tests.Support;
using static DeadlockAdvisor.Tests.Support.Golden;

namespace DeadlockAdvisor.Tests;

/// <summary>match_stats.py's pure maths on synthetic inputs, compared with what the Python app computed.</summary>
public class GoldenMatchStatsTests
{
    private static readonly JsonNode _golden = Json("match_stats_math.json");

    private static Dictionary<long, WinTotals> Totals(JsonNode? rows) =>
        Items(rows).ToDictionary(row => (long)row[0]!, row => new WinTotals((long)row[1]!, (long)row[2]!));

    private static Dictionary<long, int> Tiers(JsonNode? rows) =>
        Items(rows).ToDictionary(row => (long)row[0]!, row => (int)row[1]!);

    private static RawLift Lift(JsonNode? node) =>
        new((int)node!["matches"]!, Number(node["lift"]), Number(node["se"]));

    private static void AssertLift(JsonNode? expected, RawLift actual)
    {
        Assert.Equal((int)expected!["matches"]!, actual.Matches);
        AssertEx.Close(Number(expected["lift"]), actual.Lift);
        AssertEx.Close(Number(expected["se"]), actual.Se);
    }

    [Fact]
    public void RawLiftsMatch()
    {
        foreach (var testCase in Items(_golden["raw_lifts"]))
        {
            var lifts = MatchStatsMath.RawLifts(Totals(testCase["query"]), Totals(testCase["base"]), Tiers(testCase["tiers"]), (int)testCase["min_n"]!);

            var expected = Items(testCase["out"]).ToList();
            Assert.Equal(expected.Select(row => (long)row[0]!), lifts.Keys);
            foreach (var row in expected)
                AssertLift(row[1], lifts[(long)row[0]!]);
        }
    }

    [Fact]
    public void NoiseScaleTau2ShrinkAndPearsonMatch()
    {
        foreach (var testCase in Items(_golden["noise_scale"]))
        {
            var pairs = Items(testCase["pairs"]).Select(pair => (Lift(pair[0]), Lift(pair[1])));
            AssertEx.Close(Number(testCase["scale"]), MatchStatsMath.NoiseScale(pairs));
        }

        foreach (var testCase in Items(_golden["tau2"]))
        {
            var lifts = Items(testCase["lifts"]).Select(Lift).ToList();
            var tau2 = MatchStatsMath.EstimateTau2(lifts);
            AssertEx.Close(Number(testCase["tau2"]), tau2);
            Assert.Equal(Items(testCase["shrunk"]).Count(), lifts.Count);
            foreach (var (expected, lift) in Items(testCase["shrunk"]).Zip(lifts))
                AssertEx.Close(Number(expected), MatchStatsMath.Shrink(lift, tau2));
        }

        foreach (var testCase in Items(_golden["pearson"]))
        {
            var pairs = Items(testCase["pairs"]).Select(pair => (Number(pair[0]), Number(pair[1]))).ToList();
            AssertEx.Close(NullableNumber(testCase["r"]), MatchStatsMath.Pearson(pairs));
        }
    }

    [Fact]
    public void AnalyseFamilyMatches()
    {
        foreach (var testCase in Items(_golden["analyse_family"]))
        {
            var baseline = new Halves(Totals(testCase["base"]![0]), Totals(testCase["base"]![1]));
            var heroes = new OrderedDictionary<string, Halves>();
            foreach (var hero in Items(testCase["heroes"]))
                heroes[Text(hero[0])] = new Halves(Totals(hero[1]), Totals(hero[2]));

            var stats = MatchStatsMath.AnalyseFamily(baseline, heroes, Tiers(testCase["tiers"]));

            AssertFamilyLifts(testCase["full"], stats.Full);
            AssertFamilyLifts(testCase["first_half"], stats.Halves.First);
            AssertFamilyLifts(testCase["second_half"], stats.Halves.Second);
            Assert.Equal((int)testCase["pairs"]!, stats.Pairs);
            AssertEx.Close(Number(testCase["scale"]), stats.Scale);
            AssertEx.Close(Number(testCase["tau2"]), stats.Tau2);
            AssertEx.Close(NullableNumber(testCase["split_r"]), stats.SplitR);
            AssertEx.Close(Number(testCase["predicted_r"]), stats.PredictedR);
            AssertEx.Close(NullableNumber(testCase["reliability"]), stats.Reliability);
        }
    }

    private static void AssertFamilyLifts(JsonNode? expected, OrderedDictionary<HeroItem, RawLift> actual)
    {
        var rows = Items(expected).ToList();
        Assert.Equal(rows.Select(row => new HeroItem(Text(row[0]), (long)row[1]!)).ToHashSet(), actual.Keys.ToHashSet());
        foreach (var row in rows)
            AssertLift(row[2], actual[new HeroItem(Text(row[0]), (long)row[1]!)]);
    }

    [Fact]
    public void PatchesAndDescriptionsMatch()
    {
        var titles = Items(_golden["patch_titles"]!).Select(title => (string?)title).ToList();
        var patches = MatchStatsMath.ParsePatches(titles);
        Assert.Equal(Items(_golden["patches"]).Select(row => (Text(row[0]), (long)row[1]!, Text(row[2]))),
            patches.Select(patch => (patch.Title, patch.Start, patch.Label)));

        foreach (var row in Items(_golden["midpoint"]))
            Assert.Equal((long)row[2]!, MatchStatsMath.Midpoint((long)row[0]!, Number(row[1])));
        foreach (var row in Items(_golden["age"]))
            Assert.Equal(Text(row[1]), MatchStatsMath.Age(Number(row[0])));
    }

    /// <summary>A regression snapshot since the counts went per patch, rewritten with DEADLOCK_UPDATE_GOLDENS=1.</summary>
    [Fact]
    public void FetchResultMetaAndReportLinesMatch()
    {
        var patches = MatchStatsMath.ParsePatches(Items(_golden["patch_titles"]!).Select(title => (string?)title));
        var oneLift = new OrderedDictionary<HeroItem, RawLift> { [new HeroItem("h", 1)] = new RawLift(3000, 1.0, 0.5) };
        var empty = new OrderedDictionary<HeroItem, RawLift>();
        FamilyStats[] stats =
        [
            new(oneLift, (empty, empty), 40, 0.7912, 0.137, 0.55, 0.5),
            new(empty, (empty, empty), 0, 1.0, 0.0, null, 0.0),
        ];
        var reports = MatchStatsMath.Relations.Zip(stats).Select(pair => new FamilyReport(pair.First, patches[1], pair.Second)).ToList();
        MatchSegment[] segments =
        [
            new(patches[0], patches[0].Start, 1790296852, false, 1790296852, SliceCounts.Empty, [], []),
            new(patches[1], patches[1].Start, patches[0].Start - 1, true, 1790296852, SliceCounts.Empty, [], []),
        ];
        var result = new FetchResult([], reports, segments, null, "every match");
        if (Updating)
        {
            var updated = _golden.DeepClone().AsObject();
            updated["fetch_result_meta"] = result.Meta();
            updated["fetch_result_lines"] = new JsonArray(result.Lines().Select(line => (JsonNode)line).ToArray());
            WriteJson("match_stats_math.json", updated);
        }

        var golden = Json("match_stats_math.json");
        var expectedMeta = Encoding.UTF8.GetString(PythonJson.ToFileBytes(golden["fetch_result_meta"], ensureAscii: true));
        var actualMeta = Encoding.UTF8.GetString(PythonJson.ToFileBytes(result.Meta(), ensureAscii: true));
        Assert.Equal(expectedMeta, actualMeta);
        Assert.Equal(Items(golden["fetch_result_lines"]).Select(Text), result.Lines());
        foreach (var (row, report) in Items(golden["family_reliability"]).Zip(reports))
        {
            Assert.Equal(Text(row[0]), report.Relation);
            AssertEx.Close(NullableNumber(row[1]), report.Stats.Reliability);
            Assert.Equal(row[2]!.GetValue<bool>(), report.Kept);
        }
    }
}
