using System.Globalization;
using System.IO;
using System.Text.Json;
using DeadlockAdvisor.Models;

namespace DeadlockAdvisor.Scoring;

/// <summary>
/// One of the groups the download splits ranked matches into, by the average badge of both teams
/// (tier × 10 + subtier, so Oracle 3 is 83).
/// </summary>
/// <param name="Tier">The group's rank, numbered as /v1/assets/ranks numbers them.</param>
public sealed record RankBucket(int Tier, string Name, int MinBadge, int MaxBadge)
{
    public override string ToString() => Name;
}

/// <summary>Ranked matches whose rank is from the <paramref name="Min"/> to the <paramref name="Max"/> tier.</summary>
public sealed record RankRange(int Min, int Max);

/// <summary>One query's totals over every match, and split by rank.</summary>
/// <param name="All">Every match, ranked or not. Unranked matches have no badge, so they're in no rank group.</param>
/// <param name="ByRank">One per rank group, in <see cref="MatchCounts.Ranks"/>' order.</param>
public sealed record RankedTotals(Dictionary<long, WinTotals> All, IReadOnlyList<Dictionary<long, WinTotals>> ByRank)
{
    /// <summary>The totals over a rank range, or over every match when <paramref name="range"/> is null.</summary>
    public Dictionary<long, WinTotals> For(IReadOnlyList<RankBucket> ranks, RankRange? range)
    {
        if (range is null)
            return All;
        var totals = new Dictionary<long, WinTotals>();
        for (var i = 0; i < ranks.Count; i++)
        {
            if (ranks[i].Tier < range.Min || ranks[i].Tier > range.Max)
                continue;
            foreach (var (item, (wins, matches)) in ByRank[i])
            {
                var current = totals.GetValueOrDefault(item);
                totals[item] = new WinTotals(current.Wins + wins, current.Matches + matches);
            }
        }
        return totals;
    }
}

public sealed record RankedHalves(RankedTotals First, RankedTotals Second)
{
    public Halves For(IReadOnlyList<RankBucket> ranks, RankRange? range) => new(First.For(ranks, range), Second.For(ranks, range));
}

/// <summary>What one family's queries brought back: the baseline's and each hero's totals.</summary>
public sealed record FamilyCounts(Family Family, Patch Since, RankedHalves Baseline, OrderedDictionary<string, RankedHalves> Heroes);

/// <summary>
/// Everything a download brought back, before any analysis: the win and match totals that any rank
/// range's lifts are worked out from, so changing the range needs no new download. Saved compactly,
/// each item's totals as one array: every match's wins and matches, then each rank group's.
/// </summary>
public sealed record MatchCounts(long FetchedAt, Patch Latest, IReadOnlyList<RankBucket> Ranks, IReadOnlyList<FamilyCounts> Families)
{
    /// <summary>"Mystic+", "Initiate – Oracle", "every match".</summary>
    public string Describe(RankRange? range)
    {
        if (range is null)
            return "every match";
        var first = Ranks[0].Tier;
        var last = Ranks[^1].Tier;
        if (range.Min == first && range.Max == last)
            return "every ranked match";
        if (range.Max == last)
            return $"{Name(range.Min)}+";
        if (range.Min == first)
            return $"up to {Name(range.Max)}";
        return range.Min == range.Max ? Name(range.Min) : $"{Name(range.Min)} – {Name(range.Max)}";
    }

    private string Name(int tier) => Ranks.FirstOrDefault(rank => rank.Tier == tier)?.Name ?? $"rank {tier}";

    public byte[] ToJsonBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("fetched_at", FetchedAt);
            writer.WritePropertyName("latest_patch");
            WritePatch(writer, Latest);
            writer.WriteStartArray("ranks");
            foreach (var rank in Ranks)
            {
                writer.WriteStartObject();
                writer.WriteNumber("tier", rank.Tier);
                writer.WriteString("name", rank.Name);
                writer.WriteNumber("min_badge", rank.MinBadge);
                writer.WriteNumber("max_badge", rank.MaxBadge);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("families");
            foreach (var family in Families)
            {
                writer.WriteStartObject();
                writer.WriteString("relation", family.Family.Relation);
                writer.WriteNumber("patches", family.Family.Patches);
                writer.WritePropertyName("since");
                WritePatch(writer, family.Since);
                writer.WritePropertyName("baseline");
                WriteHalves(writer, family.Baseline);
                writer.WriteStartObject("heroes");
                foreach (var (heroId, halves) in family.Heroes)
                {
                    writer.WritePropertyName(heroId);
                    WriteHalves(writer, halves);
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WritePatch(Utf8JsonWriter writer, Patch patch)
    {
        writer.WriteStartObject();
        writer.WriteString("title", patch.Title);
        writer.WriteNumber("start", patch.Start);
        writer.WriteEndObject();
    }

    private static void WriteHalves(Utf8JsonWriter writer, RankedHalves halves)
    {
        writer.WriteStartArray();
        WriteTotals(writer, halves.First);
        WriteTotals(writer, halves.Second);
        writer.WriteEndArray();
    }

    private static void WriteTotals(Utf8JsonWriter writer, RankedTotals totals)
    {
        var items = totals.All.Keys.Concat(totals.ByRank.SelectMany(rank => rank.Keys)).Distinct();
        writer.WriteStartObject();
        foreach (var item in items)
        {
            writer.WriteStartArray(item.ToString(CultureInfo.InvariantCulture));
            foreach (var group in totals.ByRank.Prepend(totals.All))
            {
                var (wins, matches) = group.GetValueOrDefault(item);
                writer.WriteNumberValue(wins);
                writer.WriteNumberValue(matches);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    /// <summary>Throws <see cref="JsonException"/>, <see cref="KeyNotFoundException"/> or <see cref="InvalidOperationException"/> on a malformed file.</summary>
    public static MatchCounts Parse(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var ranks = root.GetProperty("ranks").EnumerateArray()
            .Select(rank => new RankBucket(
                rank.GetProperty("tier").GetInt32(), rank.GetProperty("name").GetString() ?? "",
                rank.GetProperty("min_badge").GetInt32(), rank.GetProperty("max_badge").GetInt32()))
            .ToList();

        var families = new List<FamilyCounts>();
        foreach (var family in root.GetProperty("families").EnumerateArray())
        {
            // Downloads from before lane data was dropped also hold a lane-phase family.
            if (family.TryGetProperty("scope", out var scope) && scope.GetString() != "full")
                continue;
            var heroes = new OrderedDictionary<string, RankedHalves>();
            foreach (var hero in family.GetProperty("heroes").EnumerateObject())
                heroes[hero.Name] = ReadHalves(hero.Value, ranks.Count);
            families.Add(new FamilyCounts(
                new Family(family.GetProperty("relation").GetString() ?? "", family.GetProperty("patches").GetInt32()),
                ReadPatch(family.GetProperty("since")),
                ReadHalves(family.GetProperty("baseline"), ranks.Count),
                heroes));
        }
        return new MatchCounts(root.GetProperty("fetched_at").GetInt64(), ReadPatch(root.GetProperty("latest_patch")), ranks, families);
    }

    private static Patch ReadPatch(JsonElement patch) => new(patch.GetProperty("title").GetString() ?? "", patch.GetProperty("start").GetInt64());

    private static RankedHalves ReadHalves(JsonElement halves, int rankCount) =>
        new(ReadTotals(halves[0], rankCount), ReadTotals(halves[1], rankCount));

    /// <summary>A group an item has no matches in leaves it out, as the API's answer did.</summary>
    private static RankedTotals ReadTotals(JsonElement totals, int rankCount)
    {
        var groups = Enumerable.Range(0, rankCount + 1).Select(_ => new Dictionary<long, WinTotals>()).ToList();
        foreach (var item in totals.EnumerateObject())
        {
            var id = long.Parse(item.Name, CultureInfo.InvariantCulture);
            for (var group = 0; group < groups.Count; group++)
            {
                var matches = item.Value[2 * group + 1].GetInt64();
                if (matches > 0)
                    groups[group][id] = new WinTotals(item.Value[2 * group].GetInt64(), matches);
            }
        }
        return new RankedTotals(groups[0], groups.Skip(1).ToList());
    }
}
