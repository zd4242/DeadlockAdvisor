using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services.Formats;

namespace DeadlockAdvisor.Scoring;

/// <param name="Start">Unix seconds: 00:00 UTC the day after the title's date.</param>
public sealed record Patch(string Title, long Start)
{
    /// <summary>"09-16": the date from the title, for the UI.</summary>
    public string Label => TitleDate.ToString("MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"2026-09-16": the date from the title, with its year.</summary>
    public string Date => TitleDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private DateTime TitleDate => DateTimeOffset.FromUnixTimeSeconds(Start).UtcDateTime.AddDays(-1);
}

/// <param name="Lift">Win-rate points, before shrinking.</param>
/// <param name="Se">
/// Its standard error in points: the textbook binomial one from <see cref="MatchStatsMath.RawLifts"/>, times
/// the measured <see cref="MatchStatsMath.NoiseScale"/> once calibrated.
/// </param>
public sealed record RawLift(int Matches, double Lift, double Se);

/// <summary>A game item's totals in one /item-stats answer.</summary>
public readonly record struct WinTotals(long Wins, long Matches);

/// <summary>A hero's (or the baseline's) totals over the first and second half of a window.</summary>
public sealed record Halves(Dictionary<long, WinTotals> First, Dictionary<long, WinTotals> Second)
{
    public static Halves Empty => new([], []);

    public Halves Plus(Halves other) => new(MatchStatsMath.Merge(First, other.First), MatchStatsMath.Merge(Second, other.Second));

    public Halves Minus(Halves part) => new(MatchStatsMath.Subtract(First, part.First), MatchStatsMath.Subtract(Second, part.Second));

    /// <summary>Both halves added back into the whole window.</summary>
    public Dictionary<long, WinTotals> Whole => MatchStatsMath.Merge(First, Second);
}

public readonly record struct HeroItem(string HeroId, long GameItemId);

/// <summary>Everything one family's queries say, with the standard errors already calibrated.</summary>
/// <param name="Pairs">(hero, item) pairs with a lift in both halves.</param>
/// <param name="Scale">Measured noise / textbook noise.</param>
/// <param name="SplitR">Agreement between the halves.</param>
/// <param name="PredictedR">What the calibrated noise model expects that agreement to be.</param>
public sealed record FamilyStats(
    OrderedDictionary<HeroItem, RawLift> Full,
    (OrderedDictionary<HeroItem, RawLift> First, OrderedDictionary<HeroItem, RawLift> Second) Halves,
    int Pairs,
    double Scale,
    double Tau2,
    double? SplitR,
    double PredictedR)
{
    /// <summary>The split-half agreement stepped up to the whole window (Spearman-Brown).</summary>
    public double? Reliability => SplitR is not { } r ? null : r > 0 ? 2 * r / (1 + r) : 0.0;
}

/// <param name="Relation">"against" or "as".</param>
/// <param name="Since">The oldest patch the lifts come from.</param>
public sealed record FamilyReport(string Relation, Patch Since, FamilyStats Stats)
{
    public bool Kept => Stats.Reliability is { } reliability && reliability >= MatchStatsMath.MinReliability && Stats.Tau2 > 0;

    public JsonObject Meta() => new()
    {
        ["since"] = Since.Start,
        ["since_patch"] = Since.Label,
        ["rows"] = Stats.Full.Count,
        ["noise_scale"] = NumberFormat.Round(Stats.Scale, 3),
        ["tau"] = NumberFormat.Round(Math.Sqrt(Stats.Tau2), 3),
        ["reliability"] = Stats.Reliability is { } reliability ? NumberFormat.Round(reliability, 3) : null,
        ["kept"] = Kept,
    };
}

/// <param name="Segments">What the lifts were worked out from, newest patch first.</param>
/// <param name="Rank">The rank range the lifts are for; null for every match.</param>
/// <param name="RankLabel">The range as the UI shows it: "Mystic+".</param>
public sealed record FetchResult(
    OrderedDictionary<MatchLiftKey, MatchLift> Lifts,
    IReadOnlyList<FamilyReport> Families,
    IReadOnlyList<MatchSegment> Segments,
    RankRange? Rank,
    string RankLabel)
{
    public Patch Latest => Segments[0].Patch;

    /// <summary>When the newest of the counts were fetched.</summary>
    public long FetchedAt => Segments.Max(segment => segment.FetchedAt);

    public JsonObject Meta()
    {
        var families = new JsonObject();
        foreach (var report in Families)
            families[report.Relation] = report.Meta();
        var segments = new JsonArray();
        foreach (var segment in Segments)
        {
            segments.Add(new JsonObject
            {
                ["title"] = segment.Patch.Title,
                ["label"] = segment.Patch.Label,
                ["start"] = segment.Patch.Start,
                ["until"] = segment.Until,
                ["fetched_at"] = segment.FetchedAt,
                ["complete"] = segment.Complete,
                ["ranks"] = segment.HasRanks,
            });
        }

        return new JsonObject
        {
            ["fetched_at"] = FetchedAt,
            ["latest_patch"] = new JsonObject
            {
                ["title"] = Latest.Title,
                ["label"] = Latest.Label,
                ["start"] = Latest.Start,
            },
            ["rank"] = Rank is null
                ? "all"
                : new JsonObject
                {
                    ["min"] = Rank.Min,
                    ["max"] = Rank.Max,
                    ["label"] = RankLabel,
                },
            // The API's default.
            ["match_mode"] = "ranked,unranked",
            ["min_n"] = MatchStatsMath.MinN,
            ["min_reliability"] = MatchStatsMath.MinReliability,
            ["segments"] = segments,
            ["families"] = families,
        };
    }

    public List<string> Lines()
    {
        var lines = new List<string>();
        if (Rank is not null)
            lines.Add($"Ranked matches only: {RankLabel}.");
        lines.Add("Patches: " + string.Join(", ", Segments.Select(MatchStatsMath.SegmentText)) + ".");
        foreach (var report in Families)
        {
            var stats = report.Stats;
            var reliability = stats.Reliability is { } value ? NumberFormat.Fixed(value, 2) : "n/a";
            var head = $"{MatchStatsMath.FamilyName(report.Relation)} (since patch {report.Since.Label}): ";
            if (report.Kept)
            {
                lines.Add(head + $"{stats.Full.Count} measurements, reliability {reliability}, "
                               + $"typical win-rate gain ±{NumberFormat.Fixed(Math.Sqrt(stats.Tau2), 2)} pts");
            }
            else
            {
                lines.Add(head + $"left out -- reliability {reliability} is under "
                               + $"{NumberFormat.Repr(MatchStatsMath.MinReliability)}, so the numbers would be mostly noise");
            }
        }
        return lines;
    }
}

/// <summary>
/// A second opinion from real match results: how much an item's win rate moves against a given enemy
/// hero, or on a given hero of yours. Per query (one hero, one relation):
/// <list type="number">
/// <item>delta = the item's win rate in the query − its win rate in every match of the same window,
/// so each item is compared with itself (against an enemy, without that enemy's own purchases,
/// which the query can't see);</item>
/// <item>lift = delta − the matches-weighted mean delta of the other items in the same tier (a strong
/// enemy drags every item down; tier 4 only turns up in long games);</item>
/// <item>shrunk = lift × τ² / (τ² + se²): noisy lifts are pulled toward 0 against τ², how much real
/// lifts vary, estimated from the whole family at once. The se is calibrated from two halves of the
/// window (<see cref="NoiseScale"/>), not taken on trust from the binomial formula.</item>
/// </list>
/// Everything is in win-rate points (+1.4 = 1.4 percentage points). The counts come per patch
/// (<see cref="MatchSegment"/>), dated from the patch title rather than when it was posted.
/// </summary>
public static partial class MatchStatsMath
{
    /// <summary>An item needs this many matches in a query to get a lift at all.</summary>
    public const int MinN = 2000;

    /// <summary>A family whose halves agree less than this (stepped up to the whole window) is left out.</summary>
    public const double MinReliability = 0.5;

    /// <summary>The two kinds of lift: against an enemy hero, and on your own hero.</summary>
    public static readonly IReadOnlyList<string> Relations = ["against", "as"];

    /// <summary>How the reports name a family: "Enemies" for "against", "Your hero" for "as".</summary>
    public static string FamilyName(string relation) => relation == "against" ? "Enemies" : "Your hero";

    public const string OwnIncludedNote =
        "The enemy numbers still count each enemy's own purchases (downloaded before they could be taken out): fetch again.";

    // -- ranks ------------------------------------------------------------------

    /// <summary>The highest average badge the API takes: Eternus 6.</summary>
    public const int MaxBadge = 116;

    /// <summary>
    /// The rank groups, as (first tier, last tier): two ranks each, so each has about as many matches as the
    /// next. Initiate also takes the few matches below it; the last group takes Ascendant and Eternus,
    /// too rare to count on their own.
    /// </summary>
    public static readonly IReadOnlyList<(int First, int Last)> RankGroups = [(1, 2), (3, 4), (5, 6), (7, 8), (9, 11)];

    /// <summary>One bucket per <see cref="RankGroups"/> entry, named from /v1/assets/ranks.</summary>
    public static List<RankBucket> RankBuckets(IReadOnlyDictionary<int, string> names) =>
        RankGroups.Select((group, index) => new RankBucket(
                group.First,
                group.Last,
                names.GetValueOrDefault(group.First, $"Rank {group.First}"),
                names.GetValueOrDefault(group.Last, $"Rank {group.Last}"),
                index == 0 ? 0 : group.First * 10,
                index == RankGroups.Count - 1 ? MaxBadge : group.Last * 10 + 9))
            .ToList();

    /// <summary>The rank range the stored lifts were worked out for; null for every match.</summary>
    public static RankRange? RankOf(JsonObject meta) =>
        meta["rank"] is JsonObject rank && Number(rank["min"]) is { } min && Number(rank["max"]) is { } max
            ? new RankRange((int)min, (int)max)
            : null;

    /// <summary>The rank groups the newest counts with a rank breakdown are split by; empty without one.</summary>
    public static IReadOnlyList<RankBucket> RanksOf(IReadOnlyList<MatchSegment> segments) =>
        segments.FirstOrDefault(segment => segment.HasRanks)?.Ranks ?? [];

    /// <summary>"Mystic+", "up to Oracle", "Mystic – Oracle", "every match".</summary>
    public static string DescribeRange(IReadOnlyList<RankBucket> ranks, RankRange? range)
    {
        if (range is null)
            return "every match";
        var inRange = ranks.Where(rank => rank.Overlaps(range)).ToList();
        if (inRange.Count == 0)
            return "no rank group";
        var (first, last) = (inRange[0], inRange[^1]);
        if (first == ranks[0] && last == ranks[^1])
            return "every ranked match";
        if (last == ranks[^1])
            return $"{first.FirstName}+";
        if (first == ranks[0])
            return $"up to {last.LastName}";
        return first == last ? first.Name : $"{first.FirstName} – {last.LastName}";
    }

    // -- patches ----------------------------------------------------------------

    [GeneratedRegex("([0-9]{1,2})-([0-9]{1,2})-([0-9]{4})")]
    private static partial Regex TitleDate();

    /// <summary>
    /// Patches newest first, one per date. The window starts the day after the title's date:
    /// patches go live in the evening US time, so starting that midnight UTC skips a few
    /// post-patch hours rather than letting pre-patch matches in.
    /// </summary>
    public static List<Patch> ParsePatches(IEnumerable<string?> titles)
    {
        var byStart = new OrderedDictionary<long, Patch>();
        foreach (var rawTitle in titles)
        {
            var title = rawTitle ?? "";
            var match = TitleDate().Match(title);
            if (!match.Success)
                continue;
            var month = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var day = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var year = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            if (year < 1 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
                continue;

            var start = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero).AddDays(1).ToUnixTimeSeconds();
            byStart.TryAdd(start, new Patch(title, start));
        }
        return byStart.Values.OrderBy(patch => -patch.Start).ToList();
    }

    /// <summary>The latest patch, if it's newer than the one the data was fetched under.</summary>
    public static Patch? NewerPatch(JsonObject meta, IReadOnlyList<Patch> patches)
    {
        var known = Number((meta["latest_patch"] as JsonObject)?["start"]);
        if (known is null or 0)
            return null;
        return patches.Count > 0 && patches[0].Start > known ? patches[0] : null;
    }

    /// <summary>"09-29 (2 days so far)", "09-16 (13 days)": a segment for the reports.</summary>
    public static string SegmentText(MatchSegment segment)
    {
        var days = Math.Max(1, (int)Math.Round((segment.Until - segment.From) / 86400.0));
        var span = $"{days} day{(days == 1 ? "" : "s")}{(segment.Ended ? "" : " so far")}";
        return $"{segment.Patch.Label} ({span}{(segment.HasRanks ? "" : ", no rank groups")})";
    }

    // -- queries ----------------------------------------------------------------

    /// <summary>The item-stats filters for every match: the baseline.</summary>
    public static OrderedDictionary<string, string> BaselineParams() => [];

    /// <summary>Every hero's own purchases at once, one row per (hero, item).</summary>
    public static OrderedDictionary<string, string> AsParams() => new() { ["bucket"] = "hero" };

    /// <summary>What the players facing one enemy hero bought.</summary>
    public static OrderedDictionary<string, string> AgainstParams(long enemyGameId) =>
        new() { ["enemy_hero_ids"] = enemyGameId.ToString(CultureInfo.InvariantCulture) };

    /// <summary>A query's filters narrowed to one rank group.</summary>
    public static OrderedDictionary<string, string> RankParams(OrderedDictionary<string, string> parameters, RankBucket rank) =>
        new(parameters)
        {
            ["min_average_badge"] = rank.MinBadge.ToString(CultureInfo.InvariantCulture),
            ["max_average_badge"] = rank.MaxBadge.ToString(CultureInfo.InvariantCulture),
        };

    /// <summary>An /item-stats answer as game item id → (wins, matches).</summary>
    public static Dictionary<long, WinTotals> Totals(IEnumerable<(long ItemId, long Wins, long Matches)> rows)
    {
        var totals = new Dictionary<long, WinTotals>();
        foreach (var (itemId, wins, matches) in rows)
        {
            var current = totals.GetValueOrDefault(itemId);
            totals[itemId] = new WinTotals(current.Wins + wins, current.Matches + matches);
        }
        return totals;
    }

    /// <summary>A bucketed /item-stats answer as bucket → game item id → (wins, matches).</summary>
    public static Dictionary<long, Dictionary<long, WinTotals>> BucketTotals(IEnumerable<(long Bucket, long ItemId, long Wins, long Matches)> rows) =>
        rows.GroupBy(row => row.Bucket)
            .ToDictionary(group => group.Key, group => Totals(group.Select(row => (row.ItemId, row.Wins, row.Matches))));

    /// <summary>Two sets of totals added together.</summary>
    public static Dictionary<long, WinTotals> Merge(IReadOnlyDictionary<long, WinTotals> a, IReadOnlyDictionary<long, WinTotals> b)
    {
        var merged = new Dictionary<long, WinTotals>();
        foreach (var item in a.Keys.Concat(b.Keys))
        {
            if (merged.ContainsKey(item))
                continue;
            var first = a.GetValueOrDefault(item);
            var second = b.GetValueOrDefault(item);
            merged[item] = new WinTotals(first.Wins + second.Wins, first.Matches + second.Matches);
        }
        return merged;
    }

    /// <summary>Totals without a part of them, e.g. one hero's own purchases; an item never goes below 0.</summary>
    public static Dictionary<long, WinTotals> Subtract(IReadOnlyDictionary<long, WinTotals> totals, IReadOnlyDictionary<long, WinTotals> part)
    {
        var result = new Dictionary<long, WinTotals>();
        foreach (var (item, (wins, matches)) in totals)
        {
            var (partWins, partMatches) = part.GetValueOrDefault(item);
            result[item] = new WinTotals(Math.Max(0, wins - partWins), Math.Max(0, matches - partMatches));
        }
        return result;
    }

    public static long Midpoint(long since, double now) => (long)Math.Truncate(since + (now - since) / 2);

    // -- the maths --------------------------------------------------------------

    /// <summary>
    /// Steps 1 and 2 for one query. <paramref name="tiers"/> maps game item id → tier for the items in
    /// the recommendations; anything else is ignored, including in the means. Items under <paramref name="minN"/>
    /// still count toward their tier's mean (weighted by matches, they barely move it) but get no
    /// lift of their own.
    /// </summary>
    public static OrderedDictionary<long, RawLift> RawLifts(
        IReadOnlyDictionary<long, WinTotals> query,
        IReadOnlyDictionary<long, WinTotals> baseline,
        IReadOnlyDictionary<long, int> tiers,
        int minN = MinN)
    {
        var deltas = new OrderedDictionary<long, (long N, double Delta, double Rate)>();
        foreach (var (item, (wins, n)) in query)
        {
            var (baseWins, baseN) = baseline.GetValueOrDefault(item);
            if (!tiers.ContainsKey(item) || n <= 0 || baseN <= 0)
                continue;
            var rate = (double)wins / n;
            deltas[item] = (n, 100 * (rate - (double)baseWins / baseN), rate);
        }

        var weighted = new Dictionary<int, (double Total, double Weight)>();
        foreach (var (item, (n, delta, _)) in deltas)
        {
            var tier = tiers[item];
            var (total, weight) = weighted.GetValueOrDefault(tier);
            weighted[tier] = (total + n * delta, weight + n);
        }

        var lifts = new OrderedDictionary<long, RawLift>();
        foreach (var (item, (n, delta, rate)) in deltas)
        {
            if (n < minN)
                continue;
            var (total, weight) = weighted[tiers[item]];
            var se = 100 * Math.Sqrt(rate * (1 - rate) / n);
            lifts[item] = new RawLift((int)n, delta - total / weight, se);
        }
        return lifts;
    }

    /// <summary>
    /// How far off the textbook standard errors are, measured rather than assumed: the same lift
    /// from two halves of the window differs only by noise (plus any drift between the halves,
    /// which errs on the side of more noise). Multiply every se by this.
    /// </summary>
    public static double NoiseScale(IEnumerable<(RawLift A, RawLift B)> pairs)
    {
        var list = pairs.ToList();
        var expected = 0.0;
        foreach (var (a, b) in list)
            expected += a.Se * a.Se + b.Se * b.Se;
        if (expected == 0)
            return 1.0;
        var observed = 0.0;
        foreach (var (a, b) in list)
            observed += (a.Lift - b.Lift) * (a.Lift - b.Lift);
        return Math.Sqrt(observed / expected);
    }

    /// <summary>The same lifts with every standard error multiplied by <paramref name="scale"/>.</summary>
    public static OrderedDictionary<TKey, RawLift> Rescale<TKey>(OrderedDictionary<TKey, RawLift> lifts, double scale)
        where TKey : notnull
    {
        var result = new OrderedDictionary<TKey, RawLift>();
        foreach (var (key, lift) in lifts)
            result[key] = lift with { Se = lift.Se * scale };
        return result;
    }

    /// <summary>How much real lifts vary, in points²: their spread minus the part noise alone would produce. 0 means no detectable signal.</summary>
    public static double EstimateTau2(IEnumerable<RawLift> lifts)
    {
        var list = lifts.ToList();
        if (list.Count < 2)
            return 0.0;
        var spread = 0.0;
        var noise = 0.0;
        foreach (var lift in list)
            spread += lift.Lift * lift.Lift;
        foreach (var lift in list)
            noise += lift.Se * lift.Se;
        return Math.Max(0.0, spread / list.Count - noise / list.Count);
    }

    public static double Shrink(RawLift lift, double tau2) =>
        tau2 <= 0 ? 0.0 : lift.Lift * tau2 / (tau2 + lift.Se * lift.Se);

    public static double? Pearson(IReadOnlyList<(double X, double Y)> pairs)
    {
        if (pairs.Count < 3)
            return null;
        double sumX = 0, sumY = 0;
        foreach (var (x, y) in pairs)
        {
            sumX += x;
            sumY += y;
        }
        var meanX = sumX / pairs.Count;
        var meanY = sumY / pairs.Count;
        double sxy = 0, sxx = 0, syy = 0;
        foreach (var (x, y) in pairs)
            sxy += (x - meanX) * (y - meanY);
        foreach (var (x, _) in pairs)
            sxx += (x - meanX) * (x - meanX);
        foreach (var (_, y) in pairs)
            syy += (y - meanY) * (y - meanY);
        return sxx != 0 && syy != 0 ? sxy / Math.Sqrt(sxx * syy) : null;
    }

    /// <summary>
    /// One family: every hero's lifts over the whole window and over each half, the noise calibrated
    /// from how much the halves disagree, then how much real signal is left.
    /// </summary>
    /// <param name="own">
    /// Each hero's own purchases over the same window, taken out of the baseline for that hero's lifts:
    /// an "against" query never sees the enemy's own purchases, so its baseline shouldn't either.
    /// </param>
    public static FamilyStats AnalyseFamily(
        Halves baseline, OrderedDictionary<string, Halves> heroes, IReadOnlyDictionary<long, int> tiers,
        IReadOnlyDictionary<string, Halves>? own = null)
    {
        var full = new OrderedDictionary<HeroItem, RawLift>();
        var first = new OrderedDictionary<HeroItem, RawLift>();
        var second = new OrderedDictionary<HeroItem, RawLift>();
        foreach (var (heroId, heroHalves) in heroes)
        {
            var heroBaseline = own?.GetValueOrDefault(heroId) is { } ownHalves ? baseline.Minus(ownHalves) : baseline;
            foreach (var (item, lift) in RawLifts(heroHalves.Whole, heroBaseline.Whole, tiers))
                full[new HeroItem(heroId, item)] = lift;
            // Each half has half the data, so half the floor.
            foreach (var (item, lift) in RawLifts(heroHalves.First, heroBaseline.First, tiers, MinN / 2))
                first[new HeroItem(heroId, item)] = lift;
            foreach (var (item, lift) in RawLifts(heroHalves.Second, heroBaseline.Second, tiers, MinN / 2))
                second[new HeroItem(heroId, item)] = lift;
        }

        var both = first.Keys.Where(second.ContainsKey).ToList();
        var scale = NoiseScale(both.Select(key => (first[key], second[key])));
        full = Rescale(full, scale);
        first = Rescale(first, scale);
        second = Rescale(second, scale);

        var tau2Half = EstimateTau2(first.Values.Concat(second.Values));
        var noiseHalf = 0.0;
        if (both.Count > 0)
        {
            foreach (var key in both)
                noiseHalf += first[key].Se * first[key].Se + second[key].Se * second[key].Se;
            noiseHalf /= 2 * both.Count;
        }

        return new FamilyStats(
            Full: full,
            Halves: (first, second),
            Pairs: both.Count,
            Scale: scale,
            Tau2: EstimateTau2(full.Values),
            SplitR: Pearson(both.Select(key => (first[key].Lift, second[key].Lift)).ToList()),
            PredictedR: tau2Half + noiseHalf != 0 ? tau2Half / (tau2Half + noiseHalf) : 0.0);
    }

    /// <summary>
    /// Every family's lifts from the stored counts, over one rank range (null: every match). A range
    /// needs a rank breakdown; without one anywhere, the lifts are over every match. A family whose
    /// lifts are mostly noise over that range is reported but gives no lifts. An "against" family takes
    /// each enemy's own purchases out of that enemy's baseline, from the "as" counts of the same
    /// window: otherwise an item an enemy buys a lot and does badly with would look like a counter to them.
    /// </summary>
    /// <param name="segments">At least one, newest first.</param>
    public static FetchResult Analyse(IReadOnlyList<MatchSegment> segments, RankRange? range, IEnumerable<Item> items)
    {
        var byGameId = GameItems(items);
        var tiers = byGameId.Where(pair => ItemScoring.Tiers.Contains(pair.Value.Tier)).ToDictionary(pair => pair.Key, pair => pair.Value.Tier);
        var ranks = RanksOf(segments);
        if (ranks.Count == 0)
            range = null;
        var total = Sum(segments, range);

        var lifts = new OrderedDictionary<MatchLiftKey, MatchLift>();
        var reports = new List<FamilyReport>();
        foreach (var relation in Relations)
        {
            var stats = AnalyseFamily(total.Baseline, total.Heroes(relation), tiers, relation == "against" ? total.As : null);
            var report = new FamilyReport(relation, segments[^1].Patch, stats);
            reports.Add(report);
            if (!report.Kept)
                continue;
            foreach (var (key, lift) in report.Stats.Full)
            {
                var itemId = byGameId[key.GameItemId].ItemId;
                lifts[new MatchLiftKey(itemId, key.HeroId, relation)] = new MatchLift(
                    itemId, key.HeroId, relation, lift.Matches, lift.Lift, lift.Se, Shrink(lift, report.Stats.Tau2));
            }
        }
        return new FetchResult(lifts, reports, segments, range, DescribeRange(ranks, range));
    }

    /// <summary>Every segment's counts over a rank range added up; a segment without a rank breakdown has nothing to add to a range.</summary>
    private static SliceCounts Sum(IReadOnlyList<MatchSegment> segments, RankRange? range) =>
        segments.Select(segment => segment.Slice(range)).OfType<SliceCounts>().Aggregate(SliceCounts.Empty, (sum, slice) => sum.Plus(slice));

    private static Dictionary<long, Item> GameItems(IEnumerable<Item> items)
    {
        var byGameId = new Dictionary<long, Item>();
        foreach (var item in items.Where(item => item.GameId != 0))
            byGameId[item.GameId] = item;
        return byGameId;
    }

    /// <summary>
    /// How often each hero builds each item next to the average player, from the "as" counts over one
    /// rank range: the item's share of the hero's purchases in its tier ÷ its share of everyone's.
    /// 1 is typical and 0 never bought. A hero with no purchases in a tier gets no ratio for its items.
    /// </summary>
    public static Dictionary<(string ItemId, string HeroId), double> BuildRatios(IReadOnlyList<MatchSegment> segments, RankRange? range, IEnumerable<Item> items)
    {
        var ratios = new Dictionary<(string ItemId, string HeroId), double>();
        if (segments.Count == 0)
            return ratios;
        if (RanksOf(segments).Count == 0)
            range = null;
        var total = Sum(segments, range);

        var byGameId = GameItems(items);
        var (everyone, _) = TierShares(total.Baseline, byGameId);
        foreach (var (heroId, halves) in total.As)
        {
            var (mine, tierTotals) = TierShares(halves, byGameId);
            foreach (var (itemId, (tier, share)) in everyone)
            {
                if (share > 0 && tierTotals.GetValueOrDefault(tier) > 0)
                    ratios[(itemId, heroId)] = mine.GetValueOrDefault(itemId).Share / share;
            }
        }
        return ratios;
    }

    /// <summary>Each item's share of the purchases in its tier over both halves, and each tier's purchases.</summary>
    private static (Dictionary<string, (int Tier, double Share)> Shares, Dictionary<int, long> TierTotals) TierShares(
        Halves halves, IReadOnlyDictionary<long, Item> byGameId)
    {
        var totals = halves.Whole;
        var tierTotals = new Dictionary<int, long>();
        foreach (var (gameId, (_, matches)) in totals)
        {
            if (byGameId.TryGetValue(gameId, out var item))
                tierTotals[item.Tier] = tierTotals.GetValueOrDefault(item.Tier) + matches;
        }
        var shares = new Dictionary<string, (int Tier, double Share)>();
        foreach (var (gameId, (_, matches)) in totals)
        {
            if (byGameId.TryGetValue(gameId, out var item) && tierTotals[item.Tier] > 0)
                shares[item.ItemId] = (item.Tier, (double)matches / tierTotals[item.Tier]);
        }
        return (shares, tierTotals);
    }

    // -- describing it ----------------------------------------------------------

    /// <summary>"just now", "5h ago", "3d ago".</summary>
    public static string Age(double seconds)
    {
        var hours = seconds / 3600;
        if (hours < 1)
            return "just now";
        if (hours < 48)
            return $"{(long)Math.Truncate(hours)}h ago";
        return $"{(long)Math.Truncate(hours / 24)}d ago";
    }

    /// <summary>One family's part of the meta. Downloads from before lane data was dropped keyed it "against/full".</summary>
    public static JsonObject FamilyMeta(JsonObject meta, string relation)
    {
        var families = meta["families"] as JsonObject;
        return (families?[relation] ?? families?[$"{relation}/full"]) as JsonObject ?? [];
    }

    /// <summary>"patch 09-16 · fetched 2d ago", or "" without data.</summary>
    public static string Summary(JsonObject meta, double now)
    {
        if (FetchedAt(meta) is not { } fetched)
            return "";
        var rank = RankLabel(meta) is { } label ? $" · {label}" : "";
        return $"patch {PatchLabel(meta)}{rank} · fetched {Age(now - fetched)}";
    }

    /// <summary>When the match data was fetched, in Unix seconds; null without data.</summary>
    public static double? FetchedAt(JsonObject meta) => Number(meta["fetched_at"]) is { } fetched and not 0 ? fetched : null;

    /// <summary>The patch that was current when the match data was fetched, "09-16".</summary>
    public static string PatchLabel(JsonObject meta) => Text((meta["latest_patch"] as JsonObject)?["label"]) ?? "?";

    /// <summary>The rank range the lifts were worked out for, "Mystic+"; null for every match.</summary>
    public static string? RankLabel(JsonObject meta) => (meta["rank"] as JsonObject)?["label"] is { } label ? Text(label) : null;

    /// <summary>One line per family: its lift count, or why it was left out.</summary>
    public static List<string> FamilyLines(JsonObject meta)
    {
        var lines = new List<string>();
        foreach (var relation in Relations)
        {
            var data = FamilyMeta(meta, relation);
            if (data.Count == 0)
                continue;
            var name = FamilyName(relation);
            var reliability = Number(data["reliability"]) is { } value ? NumberFormat.Fixed(value, 2) : "n/a";
            lines.Add(IsTrue(data["kept"])
                ? $"{name}: {(long)(Number(data["rows"]) ?? 0)} measurements, reliability {reliability}"
                : $"{name}: left out, reliability {reliability} is too low");
            if (IsTrue(data["kept"]) && data["own_excluded"] is JsonValue excluded && excluded.TryGetValue<bool>(out var flag) && !flag)
                lines.Add(OwnIncludedNote);
        }
        return lines;
    }

    /// <summary>
    /// What the data numbers are and where they came from, for tooltips: <see cref="DataMeaning"/>
    /// then <see cref="DataSource"/>; "" without data.
    /// </summary>
    public static string DataNote(JsonObject meta, double now) =>
        IsFetched(meta) ? DataMeaning(meta) + "\n" + DataSource(meta, now) : "";

    /// <summary>What the data numbers measure, and each family's patch window; "" without data.</summary>
    public static string DataMeaning(JsonObject meta)
    {
        if (!IsFetched(meta))
            return "";

        var lines = new List<string>
        {
            "Second opinion from real matches (deadlock-api.com): how much more often players win when they build "
            + "the item, in win-rate points (+1 is 50% → 51%). Each hero's own strength is taken out, and small "
            + "samples are pulled toward 0. It never changes the formula score.",
        };
        var against = FamilyMeta(meta, "against");
        if (IsTrue(against["kept"]))
            lines.Add($"Enemies: the gain against each enemy hero, since patch {Text(against["since_patch"]) ?? "?"}. "
                      + "These gains are small: +1 is a standout.");
        var mine = FamilyMeta(meta, "as");
        if (IsTrue(mine["kept"]))
            lines.Add($"You: the gain when your hero builds it, since patch {Text(mine["since_patch"]) ?? "?"}. "
                      + "Usually about three times bigger, and partly shows who builds it on this hero, not only what it does.");
        return string.Join("\n", lines);
    }

    /// <summary>Which matches the data comes from and how old it is: "Ranked matches only: Mystic+.\nFetched 2h ago…"; "" without data.</summary>
    public static string DataSource(JsonObject meta, double now)
    {
        if (!IsFetched(meta))
            return "";

        var lines = new List<string>();
        if (RankLabel(meta) is { } rank)
            lines.Add($"Ranked matches only: {rank}.");
        lines.Add($"Fetched {Age(now - Number(meta["fetched_at"])!.Value)} (Data → Fetch Match Stats).");
        return string.Join("\n", lines);
    }

    private static bool IsFetched(JsonObject meta) => FetchedAt(meta) is not null;

    private static bool IsTrue(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A JSON number whether it was parsed from a file or built in memory as an int or a float.</summary>
    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue<long>(out var integer))
            return integer;
        if (value.TryGetValue<int>(out var small))
            return small;
        return value.TryGetValue<double>(out var real) ? real : null;
    }
}
