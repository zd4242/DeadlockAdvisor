using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DeadlockAdvisor.Scoring;

/// <summary>
/// One of the groups the download splits ranked matches into, by the average badge of both teams
/// (tier × 10 + subtier, so Oracle 3 is 83): two ranks each, numbered as /v1/assets/ranks numbers them.
/// </summary>
public sealed record RankBucket(int FirstTier, int LastTier, string FirstName, string LastName, int MinBadge, int MaxBadge)
{
    /// <summary>"Mystic – Ritualist".</summary>
    public string Name => $"{FirstName} – {LastName}";

    public bool Overlaps(RankRange range) => FirstTier <= range.Max && LastTier >= range.Min;

    public override string ToString() => Name;
}

/// <summary>Ranked matches whose rank is from the <paramref name="Min"/> to the <paramref name="Max"/> tier.</summary>
public sealed record RankRange(int Min, int Max);

/// <summary>
/// What one segment's queries over one slice of its matches (every match, or one rank group) brought
/// back: every match's totals, each hero's own purchases ("as"), and what the players facing each hero
/// bought ("against").
/// </summary>
public sealed record SliceCounts(Halves Baseline, OrderedDictionary<string, Halves> As, OrderedDictionary<string, Halves> Against)
{
    public static SliceCounts Empty => new(Halves.Empty, [], []);

    public OrderedDictionary<string, Halves> Heroes(string relation) => relation == "as" ? As : Against;

    public SliceCounts Plus(SliceCounts other) =>
        new(Baseline.Plus(other.Baseline), Combine(As, other.As, (a, b) => a.Plus(b)), Combine(Against, other.Against, (a, b) => a.Plus(b)));

    /// <summary>These totals without <paramref name="part"/> of them, e.g. every match without one rank range.</summary>
    public SliceCounts Minus(SliceCounts part) =>
        new(Baseline.Minus(part.Baseline), Combine(As, part.As, (a, b) => a.Minus(b)), Combine(Against, part.Against, (a, b) => a.Minus(b)));

    private static OrderedDictionary<string, Halves> Combine(
        OrderedDictionary<string, Halves> a, OrderedDictionary<string, Halves> b, Func<Halves, Halves, Halves> combine)
    {
        var result = new OrderedDictionary<string, Halves>();
        foreach (var heroId in a.Keys.Concat(b.Keys))
        {
            if (!result.ContainsKey(heroId))
                result[heroId] = combine(a.GetValueOrDefault(heroId) ?? Halves.Empty, b.GetValueOrDefault(heroId) ?? Halves.Empty);
        }
        return result;
    }
}

/// <summary>
/// One patch's matches as a download brought them back: every match, and optionally each rank group,
/// over the same window, split at its midpoint into the halves that calibrate the noise. A patch that's
/// over, fetched once its matches have settled, never needs fetching again.
/// </summary>
/// <param name="From">Unix seconds: the patch's start.</param>
/// <param name="Until">Unix seconds, inclusive: the second before the next patch, or when it was fetched.</param>
/// <param name="Ended">The next patch was out when it was fetched, so <paramref name="Until"/> is the patch's end.</param>
/// <param name="Ranks">The rank groups <paramref name="ByRank"/> is split by; empty without a rank breakdown.</param>
/// <param name="ByRank">One per rank group, over the same window as <paramref name="EveryMatch"/>.</param>
public sealed record MatchSegment(
    Patch Patch,
    long From,
    long Until,
    bool Ended,
    long FetchedAt,
    SliceCounts EveryMatch,
    IReadOnlyList<RankBucket> Ranks,
    IReadOnlyList<SliceCounts> ByRank)
{
    public const int Version = 2;

    /// <summary>How long deadlock-api.com takes to take in a match, after which a finished patch's counts stop changing.</summary>
    public const long SettleSeconds = 86400;

    public bool HasRanks => ByRank.Count > 0;

    /// <summary>Over, and fetched once its matches had settled: fetching it again would bring back the same counts.</summary>
    public bool Complete => Ended && FetchedAt >= Until + SettleSeconds;

    /// <summary>Where the window splits: the first half ends the second before, the second half starts here.</summary>
    public long Midpoint => MatchStatsMath.Midpoint(From, Until);

    /// <summary>"2026-09-16.json": the patch's date, so the files sort by patch.</summary>
    public string FileName => Patch.Date + ".json";

    /// <summary>The totals over a rank range, or over every match when <paramref name="range"/> is null; null without a rank breakdown.</summary>
    public SliceCounts? Slice(RankRange? range)
    {
        if (range is null)
            return EveryMatch;
        if (!HasRanks)
            return null;
        var total = SliceCounts.Empty;
        for (var i = 0; i < Ranks.Count; i++)
        {
            if (Ranks[i].Overlaps(range))
                total = total.Plus(ByRank[i]);
        }
        return total;
    }

    public byte[] ToJsonBytes()
    {
        var groups = ByRank.Prepend(EveryMatch).ToList();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WritePropertyName("patch");
            writer.WriteStartObject();
            writer.WriteString("title", Patch.Title);
            writer.WriteNumber("start", Patch.Start);
            writer.WriteEndObject();
            writer.WriteNumber("from", From);
            writer.WriteNumber("until", Until);
            writer.WriteBoolean("ended", Ended);
            writer.WriteNumber("fetched_at", FetchedAt);
            writer.WriteStartArray("ranks");
            foreach (var rank in Ranks)
            {
                writer.WriteStartObject();
                writer.WriteNumber("first_tier", rank.FirstTier);
                writer.WriteNumber("last_tier", rank.LastTier);
                writer.WriteString("first_name", rank.FirstName);
                writer.WriteString("last_name", rank.LastName);
                writer.WriteNumber("min_badge", rank.MinBadge);
                writer.WriteNumber("max_badge", rank.MaxBadge);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("baseline");
            WriteSubject(writer, groups.Select(group => group.Baseline).ToList());
            foreach (var relation in MatchStatsMath.Relations)
            {
                writer.WriteStartObject(relation);
                foreach (var heroId in EveryMatch.Heroes(relation).Keys)
                {
                    writer.WritePropertyName(heroId);
                    WriteSubject(writer, groups.Select(group => group.Heroes(relation).GetValueOrDefault(heroId) ?? Halves.Empty).ToList());
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Each item as one array: every group's first-half wins and matches, then its second half's.</summary>
    private static void WriteSubject(Utf8JsonWriter writer, IReadOnlyList<Halves> groups)
    {
        var items = groups.SelectMany(halves => halves.First.Keys.Concat(halves.Second.Keys)).Distinct();
        writer.WriteStartObject();
        foreach (var item in items)
        {
            writer.WriteStartArray(item.ToString(CultureInfo.InvariantCulture));
            foreach (var halves in groups)
            {
                foreach (var half in new[] { halves.First, halves.Second })
                {
                    var (wins, matches) = half.GetValueOrDefault(item);
                    writer.WriteNumberValue(wins);
                    writer.WriteNumberValue(matches);
                }
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    /// <summary>Throws <see cref="JsonException"/>, <see cref="KeyNotFoundException"/>, <see cref="InvalidOperationException"/> or <see cref="FormatException"/> on a malformed file.</summary>
    public static MatchSegment Parse(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != Version)
            throw new FormatException($"Not a version {Version} match segment.");
        var ranks = root.GetProperty("ranks").EnumerateArray()
            .Select(rank => new RankBucket(
                rank.GetProperty("first_tier").GetInt32(), rank.GetProperty("last_tier").GetInt32(),
                rank.GetProperty("first_name").GetString() ?? "", rank.GetProperty("last_name").GetString() ?? "",
                rank.GetProperty("min_badge").GetInt32(), rank.GetProperty("max_badge").GetInt32()))
            .ToList();
        var groupCount = ranks.Count + 1;

        var baseline = ReadSubject(root.GetProperty("baseline"), groupCount);
        var heroes = MatchStatsMath.Relations.ToDictionary(relation => relation, relation =>
            root.GetProperty(relation).EnumerateObject().Select(hero => (hero.Name, Groups: ReadSubject(hero.Value, groupCount))).ToList());
        var slices = Enumerable.Range(0, groupCount).Select(group =>
        {
            OrderedDictionary<string, Halves> Of(string relation) =>
                new(heroes[relation].Select(hero => KeyValuePair.Create(hero.Name, hero.Groups[group])));
            return new SliceCounts(baseline[group], Of("as"), Of("against"));
        }).ToList();

        var patch = root.GetProperty("patch");
        return new MatchSegment(
            new Patch(patch.GetProperty("title").GetString() ?? "", patch.GetProperty("start").GetInt64()),
            root.GetProperty("from").GetInt64(),
            root.GetProperty("until").GetInt64(),
            root.GetProperty("ended").GetBoolean(),
            root.GetProperty("fetched_at").GetInt64(),
            slices[0],
            ranks,
            slices.Skip(1).ToList());
    }

    /// <summary>One subject's halves per group. A half an item has no matches in leaves it out, as the API's answer did.</summary>
    private static List<Halves> ReadSubject(JsonElement subject, int groupCount)
    {
        var halves = Enumerable.Range(0, groupCount).Select(_ => new Halves([], [])).ToList();
        foreach (var item in subject.EnumerateObject())
        {
            var id = long.Parse(item.Name, CultureInfo.InvariantCulture);
            for (var group = 0; group < groupCount; group++)
            {
                for (var half = 0; half < 2; half++)
                {
                    var at = 4 * group + 2 * half;
                    var matches = item.Value[at + 1].GetInt64();
                    if (matches > 0)
                        (half == 0 ? halves[group].First : halves[group].Second)[id] = new WinTotals(item.Value[at].GetInt64(), matches);
                }
            }
        }
        return halves;
    }
}
