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

/// <summary>A fitted slope with its standard error, and how many errors it sits from 0.</summary>
public readonly record struct WeightedSlope(double Value, double Se, double T);

/// <summary>A game item's (or hero's) totals in one analytics answer.</summary>
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

/// <summary>What one family's queries over one window brought back: the baseline, each hero's totals, and each hero's own purchases.</summary>
/// <param name="Own">For "against": each enemy's own purchases, which its baseline leaves out.</param>
public sealed record FamilyWindow(Halves Baseline, OrderedDictionary<string, Halves> Heroes, IReadOnlyDictionary<string, Halves>? Own = null);

/// <summary>One window's lifts for a family, over the whole of it and over each half.</summary>
public sealed record WindowLifts(
    OrderedDictionary<HeroItem, RawLift> Full,
    OrderedDictionary<HeroItem, RawLift> First,
    OrderedDictionary<HeroItem, RawLift> Second)
{
    public WindowLifts Rescale(double scale) =>
        new(MatchStatsMath.Rescale(Full, scale), MatchStatsMath.Rescale(First, scale), MatchStatsMath.Rescale(Second, scale));

    /// <summary>The same lift from each half, where both halves have one with at least <paramref name="minN"/> matches.</summary>
    public IEnumerable<(RawLift First, RawLift Second)> HalfPairs(int minN) =>
        First.Where(pair => pair.Value.Matches >= minN && Second.TryGetValue(pair.Key, out var other) && other.Matches >= minN)
            .Select(pair => (pair.Value, Second[pair.Key]));
}

/// <summary>Everything one family's queries say, with the standard errors already calibrated.</summary>
/// <param name="Pairs">(hero, item) pairs with a lift in both halves.</param>
/// <param name="Scale">Measured noise / textbook noise.</param>
/// <param name="SplitR">Agreement between the halves.</param>
/// <param name="PredictedR">What the calibrated noise model expects that agreement to be.</param>
/// <param name="Drift2">How far real lifts move from one patch to the next, in points²; null with one patch.</param>
/// <param name="Shares">Each patch's share of the weight in a typical lift, newest first; 0 for a patch with nothing to add.</param>
public sealed record FamilyStats(
    OrderedDictionary<HeroItem, RawLift> Full,
    (OrderedDictionary<HeroItem, RawLift> First, OrderedDictionary<HeroItem, RawLift> Second) Halves,
    int Pairs,
    double Scale,
    double Tau2,
    double? SplitR,
    double PredictedR,
    double? Drift2 = null,
    IReadOnlyList<double>? Shares = null)
{
    /// <summary>The split-half agreement stepped up to the whole window (Spearman-Brown).</summary>
    public double? Reliability => SplitR is not { } r ? null : r > 0 ? 2 * r / (1 + r) : 0.0;
}

/// <summary>
/// How a family's lifts move toward a rank range (<see cref="MatchStatsMath.LeanTowards"/>): how much the
/// range really differs from the rest of the matches, and each lift's share of that.
/// </summary>
/// <param name="Sigma2">How much real lifts differ between the range and the rest, in points²; 0 when it doesn't detectably.</param>
/// <param name="Reliability">How well the differences agree between the halves, stepped up to the whole window.</param>
/// <param name="Pairs">Lifts with a difference to measure.</param>
/// <param name="Shifts">What each lift moves by toward the range.</param>
public sealed record RankLean(double Sigma2, double? Reliability, int Pairs, OrderedDictionary<HeroItem, double> Shifts)
{
    /// <summary>A shift this small changes nothing anyone would see.</summary>
    public const double Visible = 0.05;

    public int Moved => Shifts.Values.Count(shift => Math.Abs(shift) >= Visible);

    /// <summary>The root mean square of the shifts that show.</summary>
    public double TypicalShift =>
        Moved == 0 ? 0 : Math.Sqrt(Shifts.Values.Where(shift => Math.Abs(shift) >= Visible).Average(shift => shift * shift));
}

/// <param name="Relation">"against" or "as".</param>
/// <param name="Since">The oldest patch the lifts come from.</param>
/// <param name="Lean">How the lifts lean toward the rank range; null over every match.</param>
public sealed record FamilyReport(string Relation, Patch Since, FamilyStats Stats, RankLean? Lean = null)
{
    public bool Kept => Stats.Reliability is { } reliability && reliability >= MatchStatsMath.MinReliability && Stats.Tau2 > 0;

    /// <summary>What leaning toward the ranks did; null over every match.</summary>
    public string? LeanText(string rankLabel) => Lean is null ? null : MatchStatsMath.LeanText(rankLabel, Lean.Moved, Lean.TypicalShift);

    public JsonObject Meta()
    {
        var meta = new JsonObject
        {
            ["since"] = Since.Start,
            ["since_patch"] = Since.Label,
            ["rows"] = Stats.Full.Count,
            ["noise_scale"] = NumberFormat.Round(Stats.Scale, 3),
            ["tau"] = NumberFormat.Round(Math.Sqrt(Stats.Tau2), 3),
            ["reliability"] = Stats.Reliability is { } reliability ? NumberFormat.Round(reliability, 3) : null,
            ["kept"] = Kept,
        };
        if (Stats.Drift2 is { } drift2)
            meta["drift"] = NumberFormat.Round(Math.Sqrt(drift2), 3);
        if (Lean is not null)
        {
            meta["lean"] = new JsonObject
            {
                ["sigma"] = NumberFormat.Round(Math.Sqrt(Lean.Sigma2), 3),
                ["reliability"] = Lean.Reliability is { } agreement ? NumberFormat.Round(agreement, 3) : null,
                ["pairs"] = Lean.Pairs,
                ["moved"] = Lean.Moved,
                ["typical"] = NumberFormat.Round(Lean.TypicalShift, 3),
            };
        }
        return meta;
    }
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

    /// <summary>A patch's share of the weight in a typical lift, over the families kept (or all of them, if none was).</summary>
    public double Share(int segment)
    {
        var families = Families.Any(report => report.Kept) ? Families.Where(report => report.Kept).ToList() : Families;
        return families.Count == 0 ? 0 : families.Average(report => report.Stats.Shares?[segment] ?? 0);
    }

    public JsonObject Meta()
    {
        var families = new JsonObject();
        foreach (var report in Families)
            families[report.Relation] = report.Meta();
        var segments = new JsonArray();
        for (var i = 0; i < Segments.Count; i++)
        {
            var segment = Segments[i];
            segments.Add(new JsonObject
            {
                ["title"] = segment.Patch.Title,
                ["label"] = segment.Patch.Label,
                ["start"] = segment.Patch.Start,
                ["until"] = segment.Until,
                ["ended"] = segment.Ended,
                ["fetched_at"] = segment.FetchedAt,
                ["complete"] = segment.Complete,
                ["ranks"] = segment.HasRanks,
                ["share"] = NumberFormat.Round(Share(i), 3),
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
            lines.Add($"Leaning toward {RankLabel}.");
        lines.Add("Patches: " + string.Join(", ", Segments.Select((segment, i) => MatchStatsMath.SegmentText(segment, Share(i)))) + ".");
        foreach (var report in Families)
        {
            var stats = report.Stats;
            var reliability = stats.Reliability is { } value ? NumberFormat.Fixed(value, 2) : "n/a";
            var head = $"{MatchStatsMath.FamilyName(report.Relation)} (since patch {report.Since.Label}): ";
            if (report.Kept)
            {
                var drift = stats.Drift2 is { } drift2 ? $", patch to patch ±{NumberFormat.Fixed(Math.Sqrt(drift2), 2)}" : "";
                lines.Add(head + $"{stats.Full.Count} measurements, reliability {reliability}, "
                               + $"typical win-rate gain ±{NumberFormat.Fixed(Math.Sqrt(stats.Tau2), 2)} pts{drift}");
                if (report.LeanText(RankLabel) is { } lean)
                    lines.Add($"  {lean}.");
            }
            else
            {
                lines.Add(head + $"left out -- reliability {reliability} is under "
                               + $"{NumberFormat.RoundTrip(MatchStatsMath.MinReliability)}, so the numbers would be mostly noise");
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
/// <item>combined = each patch's lift weighted by 1 / (se² + patches back × δ²), δ² being how far real
/// lifts move from one patch to the next (<see cref="AnalysePatches"/>): an old patch carries a young one,
/// and fades as the young one fills in.</item>
/// <item>shrunk = lift × τ² / (τ² + se²): noisy lifts are pulled toward 0 against τ², how much real
/// lifts vary, estimated from the whole family at once. The se is calibrated from two halves of each
/// window (<see cref="NoiseScale"/>), not taken on trust from the binomial formula.</item>
/// </list>
/// Everything is in win-rate points (+1.4 = 1.4 percentage points). The counts come per patch
/// (<see cref="MatchSegment"/>), dated from the patch title rather than when it was posted.
/// </summary>
public static partial class MatchStatsMath
{
    /// <summary>An item needs this many matches in a query, over every patch, to get a lift at all.</summary>
    public const int MinN = 2000;


    /// <summary>Fewer lifts in both of the newest two patches than this, and the drift between them is a guess: τ² stands in.</summary>
    public const int MinDriftPairs = 100;

    /// <summary>A family whose halves agree less than this (stepped up to the whole window) is left out.</summary>
    public const double MinReliability = 0.5;

    /// <summary>The two kinds of lift: against an enemy hero, and on your own hero.</summary>
    public static readonly IReadOnlyList<string> Relations = ["against", "as"];

    /// <summary>How the reports name a family: "Enemies" for "against", "Your hero" for "as".</summary>
    public static string FamilyName(string relation) => relation == "against" ? "Enemies" : "Your hero";

    public const string OwnIncludedNote =
        "The enemy numbers still count each enemy's own purchases (downloaded before they could be taken out): download again.";

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

    /// <summary>"09-29 (2 days so far, 18%)", "09-16 (13 days, 82%, no rank groups)": a patch and its share of the weight, for the reports.</summary>
    public static string SegmentText(MatchSegment segment, double share) =>
        $"{segment.Patch.Label} ({PatchSpan(segment.From, segment.Until, segment.Ended)}, {Percent(share)}{(segment.HasRanks ? "" : ", no rank groups")})";

    /// <summary>"2 days so far", "13 days": how long a patch's window is.</summary>
    public static string PatchSpan(double from, double until, bool ended)
    {
        var days = Math.Max(1, (int)Math.Round((until - from) / 86400.0));
        return $"{days} day{(days == 1 ? "" : "s")}{(ended ? "" : " so far")}";
    }

    private static string Percent(double share) => $"{Math.Round(share * 100):0}%";

    // -- queries ----------------------------------------------------------------

    /// <summary>The item-stats filters for every match: the baseline.</summary>
    public static OrderedDictionary<string, string> BaselineParams() => [];

    /// <summary>Every hero's own purchases at once, one row per (hero, item).</summary>
    public static OrderedDictionary<string, string> AsParams() => new() { ["bucket"] = "hero" };

    /// <summary>What the players facing one enemy hero bought.</summary>
    public static OrderedDictionary<string, string> AgainstParams(long enemyGameId) =>
        new() { ["enemy_hero_ids"] = enemyGameId.ToString(CultureInfo.InvariantCulture) };

    /// <summary>A query's filters narrowed to ranked matches: the API counts unranked ones too unless told.</summary>
    public static OrderedDictionary<string, string> RankedParams(OrderedDictionary<string, string> parameters) =>
        new(parameters) { ["match_mode"] = "ranked" };

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

    /// <summary><see cref="Pearson"/> with each point counted by its weight, such as 1/se².</summary>
    public static double? WeightedPearson(IReadOnlyList<(double X, double Y, double W)> points)
    {
        if (points.Count < 3 || Moments(points) is not { } m)
            return null;
        return m.Sxx != 0 && m.Syy != 0 ? m.Sxy / Math.Sqrt(m.Sxx * m.Syy) : null;
    }

    /// <summary>
    /// The weighted least-squares slope of Y on X, with its standard error from the weighted residuals
    /// (so lifts that vary more than their se says widen it) and t = slope ÷ se. Null with fewer than
    /// three points or no spread in X.
    /// </summary>
    public static WeightedSlope? Slope(IReadOnlyList<(double X, double Y, double W)> points)
    {
        if (points.Count < 3 || Moments(points) is not { Sxx: > 0 } m)
            return null;
        var slope = m.Sxy / m.Sxx;
        var residuals = 0.0;
        foreach (var (x, y, w) in points)
        {
            var r = y - m.MeanY - slope * (x - m.MeanX);
            residuals += w * r * r;
        }
        var se = Math.Sqrt(residuals / (points.Count - 2) / m.Sxx);
        return new WeightedSlope(slope, se, se > 0 ? slope / se : double.PositiveInfinity * Math.Sign(slope));
    }

    private static (double MeanX, double MeanY, double Sxx, double Syy, double Sxy)? Moments(IReadOnlyList<(double X, double Y, double W)> points)
    {
        double sumW = 0, sumX = 0, sumY = 0;
        foreach (var (x, y, w) in points)
        {
            sumW += w;
            sumX += w * x;
            sumY += w * y;
        }
        if (sumW <= 0)
            return null;
        var (meanX, meanY) = (sumX / sumW, sumY / sumW);
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var (x, y, w) in points)
        {
            sxx += w * (x - meanX) * (x - meanX);
            syy += w * (y - meanY) * (y - meanY);
            sxy += w * (x - meanX) * (y - meanY);
        }
        return (meanX, meanY, sxx, syy, sxy);
    }

    /// <summary>
    /// One family's lifts over one window, before the noise is calibrated: every hero's over the whole
    /// window and over each half, each half at half the floor.
    /// </summary>
    /// <param name="window">
    /// With each hero's own purchases taken out of the baseline for that hero's lifts: an "against" query
    /// never sees the enemy's own purchases, so its baseline shouldn't either.
    /// </param>
    public static WindowLifts LiftsOf(FamilyWindow window, IReadOnlyDictionary<long, int> tiers, int minN = MinN)
    {
        var full = new OrderedDictionary<HeroItem, RawLift>();
        var first = new OrderedDictionary<HeroItem, RawLift>();
        var second = new OrderedDictionary<HeroItem, RawLift>();
        foreach (var (heroId, heroHalves) in window.Heroes)
        {
            var heroBaseline = window.Own?.GetValueOrDefault(heroId) is { } ownHalves ? window.Baseline.Minus(ownHalves) : window.Baseline;
            foreach (var (item, lift) in RawLifts(heroHalves.Whole, heroBaseline.Whole, tiers, minN))
                full[new HeroItem(heroId, item)] = lift;
            foreach (var (item, lift) in RawLifts(heroHalves.First, heroBaseline.First, tiers, minN / 2))
                first[new HeroItem(heroId, item)] = lift;
            foreach (var (item, lift) in RawLifts(heroHalves.Second, heroBaseline.Second, tiers, minN / 2))
                second[new HeroItem(heroId, item)] = lift;
        }
        return new WindowLifts(full, first, second);
    }

    /// <summary>One family over one window: <see cref="AnalysePatches"/> with a single patch.</summary>
    public static FamilyStats AnalyseFamily(
        Halves baseline, OrderedDictionary<string, Halves> heroes, IReadOnlyDictionary<long, int> tiers,
        IReadOnlyDictionary<string, Halves>? own = null) =>
        AnalysePatches([new FamilyWindow(baseline, heroes, own)], tiers);

    /// <summary>
    /// One family over several patches, newest first (null for a patch with nothing over the range).
    /// Each patch's lifts are worked out against its own baseline, and the noise is calibrated from every
    /// patch's halves at once (the pairs that clear the half floor of <see cref="MinN"/>). Each lift is then
    /// the patches' lifts weighted by how much each says about the newest patch: 1 / (se² + patches back × δ²),
    /// where δ² is how far real lifts move from one patch to the next (<see cref="EstimateDrift2"/>). A young
    /// patch with few matches leans on the one before, which fades as the young one fills in, and fades
    /// faster the more the game changed. An item needs <paramref name="minN"/> matches over every patch
    /// together, and a patch counts toward it from a quarter of that.
    /// </summary>
    public static FamilyStats AnalysePatches(IReadOnlyList<FamilyWindow?> patches, IReadOnlyDictionary<long, int> tiers, int minN = MinN)
    {
        var raw = patches.Select(window => window is null ? null : LiftsOf(window, tiers, minN / 4)).ToList();
        var scale = NoiseScale(raw.OfType<WindowLifts>().SelectMany(lifts => lifts.HalfPairs(minN / 2)));
        var scaled = raw.Select(lifts => lifts?.Rescale(scale)).ToList();

        var present = Enumerable.Range(0, scaled.Count).Where(back => scaled[back] is not null).ToList();
        double? drift2 = null;
        if (present.Count > 1)
        {
            var (newer, older) = (present[0], present[1]);
            drift2 = EstimateDrift2(scaled[newer]!.Full, scaled[older]!.Full) / (older - newer)
                     ?? EstimateTau2(scaled.OfType<WindowLifts>().SelectMany(lifts => lifts.Full.Values));
        }

        var full = CombinePatches(scaled.Select(lifts => lifts?.Full).ToList(), drift2 ?? 0, minN, out var shares);
        var first = CombinePatches(scaled.Select(lifts => lifts?.First).ToList(), drift2 ?? 0, minN / 2, out _);
        var second = CombinePatches(scaled.Select(lifts => lifts?.Second).ToList(), drift2 ?? 0, minN / 2, out _);

        var both = first.Keys.Where(second.ContainsKey).ToList();
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
            PredictedR: tau2Half + noiseHalf != 0 ? tau2Half / (tau2Half + noiseHalf) : 0.0,
            Drift2: drift2,
            Shares: shares);
    }

    /// <summary>
    /// How far real lifts move from one patch to the next, in points²: the spread of the differences
    /// between two patches' lifts of the same (hero, item), minus the part their noise alone would give.
    /// Null with under <see cref="MinDriftPairs"/> lifts in both.
    /// </summary>
    public static double? EstimateDrift2(OrderedDictionary<HeroItem, RawLift> newer, OrderedDictionary<HeroItem, RawLift> older)
    {
        var pairs = newer.Where(pair => older.ContainsKey(pair.Key)).Select(pair => (New: pair.Value, Old: older[pair.Key])).ToList();
        if (pairs.Count < MinDriftPairs)
            return null;
        var spread = pairs.Sum(pair => (pair.New.Lift - pair.Old.Lift) * (pair.New.Lift - pair.Old.Lift));
        var noise = pairs.Sum(pair => pair.New.Se * pair.New.Se + pair.Old.Se * pair.Old.Se);
        return Math.Max(0.0, (spread - noise) / pairs.Count);
    }

    /// <summary>
    /// Each (hero, item)'s lifts from every patch, newest first, as one: weighted by
    /// 1 / (se² + patches back × <paramref name="drift2"/>), and kept once they add up to
    /// <paramref name="minN"/> matches. A lift with no noise at all outweighs any that has some.
    /// </summary>
    /// <param name="shares">Each patch's average share of the weight in the lifts kept.</param>
    public static OrderedDictionary<HeroItem, RawLift> CombinePatches(
        IReadOnlyList<OrderedDictionary<HeroItem, RawLift>?> patches, double drift2, int minN, out double[] shares)
    {
        var combined = new OrderedDictionary<HeroItem, RawLift>();
        var weightShares = new double[patches.Count];
        var keys = patches.OfType<OrderedDictionary<HeroItem, RawLift>>().SelectMany(lifts => lifts.Keys).Distinct();
        foreach (var key in keys)
        {
            var parts = new List<(int Back, RawLift Lift, double Variance)>();
            for (var back = 0; back < patches.Count; back++)
            {
                if (patches[back]?.GetValueOrDefault(key) is { } part)
                    parts.Add((back, part, part.Se * part.Se + back * drift2));
            }
            var matches = parts.Sum(part => part.Lift.Matches);
            if (matches < minN)
                continue;

            var exact = parts.Any(part => part.Variance == 0);
            var weights = parts.Select(part => exact ? part.Variance == 0 ? 1.0 : 0.0 : 1 / part.Variance).ToList();
            var total = weights.Sum();
            var lift = parts.Select((part, i) => weights[i] * part.Lift.Lift).Sum() / total;
            combined[key] = new RawLift(matches, lift, exact ? 0 : 1 / Math.Sqrt(total));
            for (var i = 0; i < parts.Count; i++)
                weightShares[parts[i].Back] += weights[i] / total;
        }
        shares = weightShares.Select(share => combined.Count == 0 ? 0 : share / combined.Count).ToArray();
        return combined;
    }

    /// <summary>
    /// Every family's lifts from the stored counts, every match's, leaning toward one rank range (null:
    /// none) where it plays differently (<see cref="LeanTowards"/>). The range only moves lifts, so a thin one
    /// can't empty a family: whether a family is kept is up to every match. A range needs a rank breakdown;
    /// without one anywhere, there's nothing to lean on. An "against" family takes each enemy's own
    /// purchases out of that enemy's baseline, from the "as" counts of the same window: otherwise an item an
    /// enemy buys a lot and does badly with would look like a counter to them.
    /// </summary>
    /// <param name="segments">At least one, newest first.</param>
    public static FetchResult Analyse(IReadOnlyList<MatchSegment> segments, RankRange? range, IEnumerable<Item> items)
    {
        var byGameId = GameItems(items);
        var tiers = TiersOf(byGameId);
        var ranks = RanksOf(segments);
        if (ranks.Count == 0)
            range = null;
        var everyMatch = segments.Select(segment => (SliceCounts?)segment.EveryMatch).ToList();
        var inRange = range is null ? null : segments.Select(segment => segment.Slice(range)).ToList();
        var rest = inRange?.Select((slice, i) => slice is null ? null : segments[i].EveryMatch.Minus(slice)).ToList();

        var lifts = new OrderedDictionary<MatchLiftKey, MatchLift>();
        var reports = new List<FamilyReport>();
        foreach (var relation in Relations)
        {
            var stats = AnalysePatches(Windows(everyMatch, relation), tiers);
            var lean = inRange is null
                ? null
                : LeanTowards(AnalysePatches(Windows(inRange, relation), tiers, MinN / 4), AnalysePatches(Windows(rest!, relation), tiers), stats.Tau2);
            var report = new FamilyReport(relation, segments[^1].Patch, stats, lean);
            reports.Add(report);
            if (!report.Kept)
                continue;
            foreach (var (key, lift) in stats.Full)
            {
                var itemId = byGameId[key.GameItemId].ItemId;
                var shift = lean?.Shifts.GetValueOrDefault(key) ?? 0;
                lifts[new MatchLiftKey(itemId, key.HeroId, relation)] = new MatchLift(
                    itemId, key.HeroId, relation, lift.Matches, lift.Lift, lift.Se, Shrink(lift, stats.Tau2) + shift, shift);
            }
        }
        return new FetchResult(lifts, reports, segments, range, DescribeRange(ranks, range));
    }

    /// <summary>One family's side of each patch's counts; null where the patch has none.</summary>
    private static List<FamilyWindow?> Windows(IEnumerable<SliceCounts?> slices, string relation) =>
        slices.Select(slice => slice is null
            ? null
            : new FamilyWindow(slice.Baseline, slice.Heroes(relation), relation == "against" ? slice.As : null)).ToList();

    /// <summary>
    /// How far each lift moves toward a rank range. Every match mixes the range (R) and the rest (C), so
    /// the range's lift is every match's plus π_C × (L_R − L_C), π_C being the rest's share of the
    /// matches. The difference is shrunk like a lift: toward 0 against σ², how much real differences vary,
    /// estimated from the whole family. When the differences don't agree between the halves, they're noise
    /// and σ² is 0, so a range too thin to say anything leaves every match's numbers as they are.
    /// <para>
    /// The halves can't see all the noise in a thin range: the same few players play on both days, so
    /// their habits agree with themselves and look real. Measured on real matches, lifts spread wider the
    /// fewer the matches (enemies: ±1.5 points at Phantom+, ±0.5 over every match). Real lifts are taken
    /// to spread as widely at every rank as over every match (<paramref name="tau2"/>), so the extra spread
    /// of either side counts as noise in each difference.
    /// </para>
    /// </summary>
    /// <param name="inRange">The family over the range, with a lower floor: its lifts only nudge.</param>
    /// <param name="rest">The family over every other match, unranked ones included.</param>
    /// <param name="tau2">How much real lifts vary over every match.</param>
    public static RankLean LeanTowards(FamilyStats inRange, FamilyStats rest, double tau2)
    {
        var hidden = Math.Max(0, inRange.Tau2 - tau2) + Math.Max(0, rest.Tau2 - tau2);
        List<(HeroItem Key, double D, double Se2, double RestShare)> Differences(
            OrderedDictionary<HeroItem, RawLift> range, OrderedDictionary<HeroItem, RawLift> others) =>
            range.Where(pair => others.ContainsKey(pair.Key))
                .Select(pair =>
                {
                    var (r, c) = (pair.Value, others[pair.Key]);
                    return (pair.Key, r.Lift - c.Lift, r.Se * r.Se + c.Se * c.Se + hidden, (double)c.Matches / (r.Matches + c.Matches));
                })
                .ToList();

        var full = Differences(inRange.Full, rest.Full);
        var sigma2 = full.Count < 2 ? 0.0 : Math.Max(0.0, full.Average(diff => diff.D * diff.D) - full.Average(diff => diff.Se2));

        var first = Differences(inRange.Halves.First, rest.Halves.First).ToDictionary(diff => diff.Key, diff => diff.D);
        var second = Differences(inRange.Halves.Second, rest.Halves.Second).ToDictionary(diff => diff.Key, diff => diff.D);
        var splitR = Pearson(first.Keys.Where(second.ContainsKey).Select(key => (first[key], second[key])).ToList());
        double? reliability = splitR is not { } r ? null : r > 0 ? 2 * r / (1 + r) : 0.0;
        if (reliability is not { } value || value < MinReliability)
            sigma2 = 0;

        var shifts = new OrderedDictionary<HeroItem, double>();
        foreach (var (key, d, se2, restShare) in full)
            shifts[key] = sigma2 <= 0 ? 0 : restShare * d * sigma2 / (sigma2 + se2);
        return new RankLean(sigma2, reliability, full.Count, shifts);
    }

    /// <summary>Game item id → tier, for the items in <see cref="ItemScoring.Tiers"/>: the ones lifts are measured for.</summary>
    public static Dictionary<long, int> Tiers(IEnumerable<Item> items) => TiersOf(GameItems(items));

    private static Dictionary<long, int> TiersOf(Dictionary<long, Item> byGameId) =>
        byGameId.Where(pair => ItemScoring.Tiers.Contains(pair.Value.Tier)).ToDictionary(pair => pair.Key, pair => pair.Value.Tier);

    private static Dictionary<long, Item> GameItems(IEnumerable<Item> items)
    {
        var byGameId = new Dictionary<long, Item>();
        foreach (var item in items.Where(item => item.GameId != 0))
            byGameId[item.GameId] = item;
        return byGameId;
    }

    /// <summary>
    /// How often each hero builds each item next to the average player, from the "as" counts over every
    /// match: the item's share of the hero's purchases in its tier ÷ its share of everyone's. 1 is typical
    /// and 0 never bought. A hero with no purchases in a tier gets no ratio for its items. Ranks don't come
    /// into it: how a hero is built barely changes with rank, and a thin range would mark items rarely
    /// built by chance. Each patch's shares count by that patch's share of the weight in the lifts
    /// (<paramref name="weights"/>, by patch start); without one, by its purchases, as if the patches were one.
    /// </summary>
    public static Dictionary<(string ItemId, string HeroId), double> BuildRatios(
        IReadOnlyList<MatchSegment> segments, IEnumerable<Item> items, IReadOnlyDictionary<long, double>? weights = null)
    {
        var ratios = new Dictionary<(string ItemId, string HeroId), double>();
        if (segments.Count == 0)
            return ratios;
        var slices = segments.Select(segment => (Slice: segment.EveryMatch, Weight: weights?.GetValueOrDefault(segment.Patch.Start))).ToList();

        var byGameId = GameItems(items);
        var everyone = BlendedShares(slices.Select(part => (part.Slice.Baseline, part.Weight)), byGameId);
        var heroes = slices.SelectMany(part => part.Slice.As.Keys).Distinct();
        foreach (var heroId in heroes)
        {
            var mine = BlendedShares(slices.Select(part => (part.Slice.As.GetValueOrDefault(heroId) ?? Halves.Empty, part.Weight)), byGameId);
            foreach (var (itemId, (tier, share)) in everyone.Shares)
            {
                if (share > 0 && mine.Tiers.Contains(tier))
                    ratios[(itemId, heroId)] = mine.Shares.GetValueOrDefault(itemId).Share / share;
            }
        }
        return ratios;
    }

    /// <summary>
    /// Each item's share of the purchases in its tier, averaged over the patches with purchases in that
    /// tier, each counting by its weight (or, without one, by its purchases there); and the tiers with any.
    /// </summary>
    private static (Dictionary<string, (int Tier, double Share)> Shares, HashSet<int> Tiers) BlendedShares(
        IEnumerable<(Halves Counts, double? Weight)> patches, IReadOnlyDictionary<long, Item> byGameId)
    {
        var weighted = new Dictionary<string, (int Tier, double Sum)>();
        var tierWeights = new Dictionary<int, double>();
        foreach (var (counts, weight) in patches)
        {
            var totals = counts.Whole;
            var tierTotals = new Dictionary<int, long>();
            foreach (var (gameId, (_, matches)) in totals)
            {
                if (byGameId.TryGetValue(gameId, out var item))
                    tierTotals[item.Tier] = tierTotals.GetValueOrDefault(item.Tier) + matches;
            }
            foreach (var (tier, total) in tierTotals.Where(pair => pair.Value > 0))
                tierWeights[tier] = tierWeights.GetValueOrDefault(tier) + (weight ?? total);
            foreach (var (gameId, (_, matches)) in totals)
            {
                if (!byGameId.TryGetValue(gameId, out var item) || tierTotals[item.Tier] <= 0)
                    continue;
                var share = (double)matches / tierTotals[item.Tier] * (weight ?? tierTotals[item.Tier]);
                weighted[item.ItemId] = (item.Tier, weighted.GetValueOrDefault(item.ItemId).Sum + share);
            }
        }
        var shares = weighted
            .Where(pair => tierWeights.GetValueOrDefault(pair.Value.Tier) > 0)
            .ToDictionary(pair => pair.Key, pair => (pair.Value.Tier, pair.Value.Sum / tierWeights[pair.Value.Tier]));
        return (shares, tierWeights.Where(pair => pair.Value > 0).Select(pair => pair.Key).ToHashSet());
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

    /// <summary>Each patch's share of the weight in the lifts, by patch start; null for lifts from before patches were weighed.</summary>
    public static Dictionary<long, double>? SegmentShares(JsonObject meta)
    {
        if (meta["segments"] is not JsonArray segments)
            return null;
        var shares = new Dictionary<long, double>();
        foreach (var segment in segments.OfType<JsonObject>())
        {
            if (Number(segment["start"]) is not { } start || Number(segment["share"]) is not { } share)
                return null;
            shares[(long)start] = share;
        }
        return shares;
    }

    /// <summary>
    /// One line per patch the lifts come from, newest first: ("Patch 09-29", "2 days so far · 18%"). Lifts
    /// from before the counts went per patch have only the patch they were fetched under.
    /// </summary>
    public static List<(string Label, string Value)> PatchFacts(JsonObject meta)
    {
        if (meta["segments"] is not JsonArray segments)
            return [("Patch", PatchLabel(meta))];
        return segments.OfType<JsonObject>().Select(segment =>
        {
            var span = PatchSpan(Number(segment["start"]) ?? 0, Number(segment["until"]) ?? 0, IsTrue(segment["ended"]));
            var share = Number(segment["share"]) is { } value ? $" · {Percent(value)}" : "";
            return ($"Patch {Text(segment["label"]) ?? "?"}", span + share);
        }).ToList();
    }


    /// <summary>"Phantom+ moves 37 of them, typically ±0.15 pts", or that the ranks make no difference.</summary>
    public static string LeanText(string rankLabel, int moved, double typical) =>
        moved == 0
            ? $"{rankLabel} isn't detectably different from every match, so these are every match's numbers"
            : $"{rankLabel} moves {moved} of them, typically ±{NumberFormat.Fixed(typical, 2)} pts";

    /// <summary>One line per family the lifts lean toward a rank range in: "Enemies: Phantom+ moves 37 of them…"; empty over every match.</summary>
    public static List<string> LeanLines(JsonObject meta)
    {
        if (RankLabel(meta) is not { } rank)
            return [];
        return Relations
            .Select(relation => (relation, Lean: FamilyMeta(meta, relation)["lean"] as JsonObject))
            .Where(part => part.Lean is not null)
            .Select(part => $"{FamilyName(part.relation)}: "
                            + LeanText(rank, (int)(Number(part.Lean!["moved"]) ?? 0), Number(part.Lean["typical"]) ?? 0))
            .ToList();
    }

    /// <summary>"Patch to patch: enemies ±0.31, your hero ±0.92 pts"; null with one patch.</summary>
    public static string? DriftLine(JsonObject meta)
    {
        var parts = Relations
            .Select(relation => (relation, Drift: Number(FamilyMeta(meta, relation)["drift"])))
            .Where(part => part.Drift is not null)
            .Select(part => $"{(part.relation == "against" ? "enemies" : "your hero")} ±{NumberFormat.Fixed(part.Drift!.Value, 2)}")
            .ToList();
        return parts.Count == 0 ? null : $"Patch to patch: {string.Join(", ", parts)} pts";
    }

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
            lines.Add($"You (hero fit): how much more your hero wins with it than everyone who builds it, since patch {Text(mine["since_patch"]) ?? "?"}. "
                      + "Usually about three times bigger, and partly shows who builds it on this hero, not only what it does. "
                      + "A low one doesn't make the item a bad buy, only one your hero gains less from than from its usual picks.");
        return string.Join("\n", lines);
    }

    /// <summary>Which matches the data comes from and how old it is: "Leaning toward Mystic+ where it plays differently.\nFetched 2h ago…"; "" without data.</summary>
    public static string DataSource(JsonObject meta, double now)
    {
        if (!IsFetched(meta))
            return "";

        var lines = new List<string>();
        if (RankLabel(meta) is { } rank)
            lines.Add($"Leaning toward {rank} where it plays differently.");
        lines.Add($"Fetched {Age(now - Number(meta["fetched_at"])!.Value)} (Data → Check for Updates).");
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
