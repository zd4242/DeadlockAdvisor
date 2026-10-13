using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Ui;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Tests;

/// <summary>One player of a finished match: what they bought and how their damage to the other team divided.</summary>
public sealed record FitRow(long MatchId, string HeroId, int Duration, long Bullet, long Ability, long Melee, IReadOnlyList<long> Items);

/// <summary>
/// WP29's measurement of <see cref="BuildShifts.Alpha"/>: how far a player's damage mix follows the way their purchases divide among the
/// shops, against what their hero usually buys. Not a test: it does nothing unless DEADLOCK_BUILD_FIT points at a data folder (the app's
/// <c>data</c> folder, read and never written: it needs the items, the hero ratings and the downloaded match counts). It samples
/// DEADLOCK_BUILD_FIT_MATCHES (120 unless set) finished matches from the last week, one request each (about 1.1 MB), spaced out, and
/// writes <c>mockups/build_shift_fit/rows.csv</c> (no accounts: a match, a hero, damage by kind and the items) and <c>report.md</c>. With
/// rows.csv already there it measures again without asking for anything, unless DEADLOCK_BUILD_FIT_REFETCH is set (start over) or
/// DEADLOCK_BUILD_FIT_MORE=<n> (add n matches the file doesn't have). Nothing is committed
/// and nothing here ships.
/// </summary>
public class BuildShiftFit
{
    public static string OutDir => Path.Combine(UiHarness.RepoRoot(), "mockups", "build_shift_fit");

    private const string Api = "https://api.deadlock-api.com";
    private const int MinDuration = 1200;
    private const int Bootstraps = 2000;

    [Fact]
    public async Task MeasureAlpha()
    {
        if (Environment.GetEnvironmentVariable("DEADLOCK_BUILD_FIT") is not { Length: > 0 } folder)
            return;
        var store = DataStore.Load(folder);
        Directory.CreateDirectory(OutDir);
        var csv = Path.Combine(OutDir, "rows.csv");
        var matches = int.TryParse(Environment.GetEnvironmentVariable("DEADLOCK_BUILD_FIT_MATCHES"), out var wanted) ? wanted : 120;
        var cached = File.Exists(csv) ? File.ReadAllLines(csv).Select(Parse).ToList() : [];
        if (cached.Count == 0 || Environment.GetEnvironmentVariable("DEADLOCK_BUILD_FIT_REFETCH") is { Length: > 0 })
            File.WriteAllLines(csv, (await Sample(store, matches, [])).Select(Format));
        else if (int.TryParse(Environment.GetEnvironmentVariable("DEADLOCK_BUILD_FIT_MORE"), out var more) && more > 0)
            File.WriteAllLines(csv, cached.Concat(await Sample(store, more, cached.Select(row => row.MatchId).ToHashSet())).Select(Format));

        var rows = File.ReadAllLines(csv).Select(Parse).ToList();
        File.WriteAllText(Path.Combine(OutDir, "report.md"), Report(store, rows));
    }

    private static string Format(FitRow row) =>
        string.Join(',', row.MatchId, row.HeroId, row.Duration, row.Bullet, row.Ability, row.Melee, string.Join(';', row.Items));

    private static FitRow Parse(string line)
    {
        var cells = line.Split(',');
        return new FitRow(long.Parse(cells[0], CultureInfo.InvariantCulture), cells[1], int.Parse(cells[2], CultureInfo.InvariantCulture),
            long.Parse(cells[3], CultureInfo.InvariantCulture), long.Parse(cells[4], CultureInfo.InvariantCulture),
            long.Parse(cells[5], CultureInfo.InvariantCulture),
            cells[6].Length == 0 ? [] : cells[6].Split(';').Select(item => long.Parse(item, CultureInfo.InvariantCulture)).ToList());
    }

    // -- sampling ---------------------------------------------------------------

    private static async Task<List<FitRow>> Sample(DataStore store, int matches, HashSet<long> already)
    {
        using var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("deadlock-advisor/dev (+https://github.com/zd4242/DeadlockAdvisor) (build shift fit)");
        var heroes = store.Heroes.Values.Where(hero => hero.GameId != 0).ToDictionary(hero => hero.GameId, hero => hero.HeroId);
        var random = new Random(29 + already.Count);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = new List<FitRow>();
        var seen = new HashSet<long>(already);
        var attempts = 0;
        while (seen.Count - already.Count < matches && attempts++ < matches * 3)
        {
            // Ten minutes at a random time between one and eight days ago, a few matches from each.
            var from = now - 86400 - random.NextInt64(7 * 86400);
            var list = await Get(client, $"{Api}/v1/matches/metadata?limit=3&min_unix_timestamp={from}&max_unix_timestamp={from + 600}"
                                         + $"&min_duration_s={MinDuration}&game_mode=normal");
            foreach (var id in JsonDocument.Parse(list).RootElement.EnumerateArray().Select(entry => entry.GetProperty("match_id").GetInt64()))
            {
                if (seen.Count - already.Count >= matches || !seen.Add(id))
                    continue;
                rows.AddRange(Rows(await Get(client, $"{Api}/v1/matches/{id}/metadata"), heroes));
            }
        }
        return rows;
    }

    private static async Task<string> Get(HttpClient client, string url)
    {
        await Task.Delay(500);
        return await client.GetStringAsync(url);
    }

    /// <summary>A match's players: damage to the other team by kind (the cumulative series' last value) and every shop item bought.</summary>
    public static IEnumerable<FitRow> Rows(string metadata, IReadOnlyDictionary<long, string> heroes)
    {
        var info = JsonDocument.Parse(metadata).RootElement.GetProperty("match_info");
        var duration = info.GetProperty("duration_s").GetInt32();
        var matchId = info.GetProperty("match_id").GetInt64();
        var players = info.GetProperty("players").EnumerateArray().ToDictionary(
            player => player.GetProperty("player_slot").GetInt32(), player => player);
        var matrix = info.GetProperty("damage_matrix");
        var names = matrix.GetProperty("source_details").GetProperty("source_name").EnumerateArray().Select(name => name.GetString()).ToList();
        foreach (var dealer in matrix.GetProperty("damage_dealers").EnumerateArray())
        {
            var slot = dealer.GetProperty("dealer_player_slot").GetInt32();
            if (!players.TryGetValue(slot, out var player) || !heroes.TryGetValue(player.GetProperty("hero_id").GetInt64(), out var heroId))
                continue;
            var team = player.GetProperty("team").GetInt32();
            var byKind = new Dictionary<string, long>();
            foreach (var source in dealer.GetProperty("damage_sources").EnumerateArray())
            {
                var kind = names[source.GetProperty("source_details_index").GetInt32()];
                if (kind is not ("Bullet" or "Ability" or "Melee"))
                    continue;
                foreach (var target in source.GetProperty("damage_to_players").EnumerateArray())
                {
                    if (!players.TryGetValue(target.GetProperty("target_player_slot").GetInt32(), out var other)
                        || other.GetProperty("team").GetInt32() == team)
                        continue;
                    var series = target.GetProperty("damage");
                    if (series.GetArrayLength() > 0)
                        byKind[kind] = byKind.GetValueOrDefault(kind) + series[series.GetArrayLength() - 1].GetInt64();
                }
            }
            var items = player.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("item_id").GetInt64()).ToList();
            yield return new FitRow(matchId, heroId, duration, byKind.GetValueOrDefault("Bullet"), byKind.GetValueOrDefault("Ability"),
                byKind.GetValueOrDefault("Melee"), items);
        }
    }

    // -- the fit ------------------------------------------------------------------

    /// <summary>
    /// One player: how far what their build does leans from their hero's usual (<paramref name="Lean"/>, by each item's lean; and
    /// <paramref name="ColourLean"/>, by the shop's colour alone), the damage mix that came with it, and the shop items they bought.
    /// </summary>
    public sealed record Point(
        long MatchId, string HeroId, double Lean, double ColourLean, double BulletShare, double SpiritShare, double Spent,
        IReadOnlyList<string> Items);

    public static List<Point> Points(DataStore store, IEnumerable<FitRow> rows)
    {
        var known = store.Items.Values.Where(item => item.GameId != 0 && item.Tier is >= 1 and <= 4 && ItemTemplates.CategoryOf(item.Category) is not null)
            .ToDictionary(item => item.GameId);
        var points = new List<Point>();
        foreach (var row in rows)
        {
            var total = row.Bullet + row.Ability + row.Melee;
            if (total <= 0 || !store.TypicalBuilds.TryGetValue(row.HeroId, out var usual))
                continue;
            var (spent, leaning, colour) = (0.0, 0.0, 0.0);
            var bought = new List<string>();
            foreach (var item in row.Items.Where(known.ContainsKey).Select(gameId => known[gameId]))
            {
                var cost = HeroBuild.TierCost(item.Tier);
                spent += cost;
                leaning += cost * store.LeanOf(item.ItemId).Lean;
                colour += cost * ItemLean.OfShop(item.Category);
                bought.Add(item.ItemId);
            }
            if (spent <= 0)
                continue;
            var usualColour = usual.Shares[ShopCategory.Weapon] - usual.Shares[ShopCategory.Spirit];
            points.Add(new Point(row.MatchId, row.HeroId, leaning / spent - usual.Lean, colour / spent - usualColour,
                100.0 * row.Bullet / total, 100.0 * row.Ability / total, spent, bought));
        }
        return points;
    }

    /// <summary>
    /// The slope of y on x within each hero (both measured from the hero's own average, so what a hero is rated at doesn't count, only how
    /// this player differs from it): Σ x̃ỹ ÷ Σ x̃². Heroes with fewer than <paramref name="minPerHero"/> players are left out.
    /// </summary>
    public static double Slope(IReadOnlyList<Point> points, Func<Point, double> y, Func<Point, double> x, int minPerHero = 3)
    {
        var (numerator, denominator) = (0.0, 0.0);
        foreach (var hero in points.GroupBy(point => point.HeroId).Where(group => group.Count() >= minPerHero))
        {
            var (meanX, meanY) = (hero.Average(x), hero.Average(y));
            foreach (var point in hero)
            {
                numerator += (x(point) - meanX) * (y(point) - meanY);
                denominator += (x(point) - meanX) * (x(point) - meanX);
            }
        }
        return denominator == 0 ? 0 : numerator / denominator;
    }

    /// <summary>The share of the within-hero variance of y that x accounts for.</summary>
    public static double RSquared(IReadOnlyList<Point> points, Func<Point, double> y, Func<Point, double> x, int minPerHero = 3)
    {
        var slope = Slope(points, y, x, minPerHero);
        var (explained, total) = (0.0, 0.0);
        foreach (var hero in points.GroupBy(point => point.HeroId).Where(group => group.Count() >= minPerHero))
        {
            var (meanX, meanY) = (hero.Average(x), hero.Average(y));
            foreach (var point in hero)
            {
                explained += Math.Pow(slope * (x(point) - meanX), 2);
                total += Math.Pow(y(point) - meanY, 2);
            }
        }
        return total == 0 ? 0 : explained / total;
    }

    /// <summary>The slope with a 95% interval from resampling whole matches, since a match's ten players aren't independent.</summary>
    public static (double Slope, double Low, double High) WithInterval(
        IReadOnlyList<Point> points, Func<Point, double> y, Func<Point, double> x, int seed = 1)
    {
        var byMatch = points.GroupBy(point => point.MatchId).Select(group => group.ToList()).ToList();
        var random = new Random(seed);
        var slopes = new List<double>();
        for (var i = 0; i < Bootstraps; i++)
        {
            var resampled = Enumerable.Range(0, byMatch.Count).SelectMany(_ => byMatch[random.Next(byMatch.Count)]).ToList();
            slopes.Add(Slope(resampled, y, x));
        }
        slopes.Sort();
        return (Slope(points, y, x), slopes[(int)(0.025 * slopes.Count)], slopes[(int)(0.975 * slopes.Count)]);
    }

    /// <summary>How much more of the within-hero variance of y the lean by items accounts for than the lean by shop, with a 95% interval from resampling matches.</summary>
    public static (double Gain, double Low, double High) GainOverShop(IReadOnlyList<Point> points, Func<Point, double> y, int seed = 2)
    {
        double Gain(IReadOnlyList<Point> subset) => RSquared(subset, y, point => point.Lean) - RSquared(subset, y, point => point.ColourLean);
        var byMatch = points.GroupBy(point => point.MatchId).Select(group => group.ToList()).ToList();
        var random = new Random(seed);
        var gains = Enumerable.Range(0, Bootstraps)
            .Select(_ => Gain(Enumerable.Range(0, byMatch.Count).SelectMany(_ => byMatch[random.Next(byMatch.Count)]).ToList()))
            .Order()
            .ToList();
        return (Gain(points), gains[(int)(0.025 * gains.Count)], gains[(int)(0.975 * gains.Count)]);
    }

    /// <summary>
    /// For each item bought often enough: how much more bullet damage share its buyers had than the lean of their builds explains, within
    /// their hero, against everyone else, with a Welch t. A lead for a lean override, never applied: the lean says where it points.
    /// </summary>
    public static List<(string ItemId, int Buyers, double Extra, double T)> ItemEffects(IReadOnlyList<Point> points, int minBuyers = 25)
    {
        var slope = Slope(points, point => point.BulletShare, point => point.Lean);
        var residual = new Dictionary<Point, double>();
        foreach (var hero in points.GroupBy(point => point.HeroId).Where(group => group.Count() >= 3))
        {
            var (meanX, meanY) = (hero.Average(point => point.Lean), hero.Average(point => point.BulletShare));
            foreach (var point in hero)
                residual[point] = point.BulletShare - meanY - slope * (point.Lean - meanX);
        }
        var found = new List<(string, int, double, double)>();
        foreach (var itemId in points.SelectMany(point => point.Items).Distinct())
        {
            var (with, without) = (new List<double>(), new List<double>());
            foreach (var (point, left) in residual)
                (point.Items.Contains(itemId) ? with : without).Add(left);
            if (with.Count < minBuyers || without.Count < minBuyers)
                continue;
            var extra = with.Average() - without.Average();
            var error = Math.Sqrt(Variance(with) / with.Count + Variance(without) / without.Count);
            found.Add((itemId, with.Count, extra, error == 0 ? 0 : extra / error));
        }
        return found;
    }

    private static double Variance(List<double> values)
    {
        var mean = values.Average();
        return values.Sum(value => (value - mean) * (value - mean)) / Math.Max(1, values.Count - 1);
    }

    private static string Report(DataStore store, IReadOnlyList<FitRow> rows)
    {
        var points = Points(store, rows);
        var text = new StringBuilder();
        text.AppendLine("# Build shift fit");
        text.AppendLine();
        text.AppendLine($"{rows.Select(row => row.MatchId).Distinct().Count()} matches, {rows.Count} players, {points.Count} with a usual build "
                        + $"({points.Select(point => point.HeroId).Distinct().Count()} heroes). Matches of at least {MinDuration / 60} minutes; damage to the other team's "
                        + "heroes by kind (bullet, ability, melee); the build is every shop item bought, at its tier's cost.");
        text.AppendLine();
        text.AppendLine("Slope: points of damage share (a hundred is all of it) per unit of lean, within each hero, 95% interval from resampling matches. "
                        + "R²: the share of the within-hero variance the lean accounts for. **Items** takes each item's lean from the stat table "
                        + $"({store.StatLeans.Count} rows, {store.ItemLeanOverrides.Count} overrides); **shop** counts weapon items +1, spirit -1, vitality 0.");
        text.AppendLine();
        text.AppendLine($"Shipped: Alpha = {BuildShifts.Alpha}, FullRampSouls = {BuildShifts.FullRampSouls}.");
        text.AppendLine();
        text.AppendLine("| | players | bullet, items | R² | bullet, shop | R² | spirit, items | spirit, shop |");
        text.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var (label, subset) in Brackets(points))
        {
            var items = WithInterval(subset, point => point.BulletShare, point => point.Lean);
            var shop = WithInterval(subset, point => point.BulletShare, point => point.ColourLean);
            var itemsSpirit = WithInterval(subset, point => point.SpiritShare, point => point.Lean);
            var shopSpirit = WithInterval(subset, point => point.SpiritShare, point => point.ColourLean);
            text.AppendLine($"| {label} | {subset.Count} | {Slope3(items)} | {RSquared(subset, point => point.BulletShare, point => point.Lean):0.000} "
                            + $"| {Slope3(shop)} | {RSquared(subset, point => point.BulletShare, point => point.ColourLean):0.000} "
                            + $"| {Slope3(itemsSpirit)} | {Slope3(shopSpirit)} |");
        }
        text.AppendLine();
        var gain = GainOverShop(points, point => point.BulletShare);
        text.AppendLine($"R² gained over the shop's colour, bullet share: {gain.Gain:+0.000;-0.000} (95% interval {gain.Low:+0.000;-0.000} to {gain.High:+0.000;-0.000}).");
        text.AppendLine();
        foreach (var (label, x) in new (string, Func<Point, double>)[] { ("items", point => point.Lean), ("shop", point => point.ColourLean) })
        {
            var lean = points.Select(x).Order().ToList();
            text.AppendLine($"Lean by {label}: 5th to 95th percentile {lean[(int)(0.05 * lean.Count)]:0.00} to {lean[(int)(0.95 * lean.Count)]:0.00}; "
                            + $"spread {Math.Sqrt(lean.Average(value => value * value)):0.00}.");
        }
        text.AppendLine();
        text.AppendLine($"Damage mix of the average player {points.Average(point => point.BulletShare):0.0}% bullet, {points.Average(point => point.SpiritShare):0.0}% ability.");

        var disagree = ItemEffects(points)
            .Where(entry => Math.Abs(entry.T) >= 3 && Math.Sign(entry.Extra) != Math.Sign(store.LeanOf(entry.ItemId).Lean) && store.LeanOf(entry.ItemId).Lean != 0)
            .OrderByDescending(entry => Math.Abs(entry.T))
            .ToList();
        text.AppendLine();
        text.AppendLine("## Items whose buyers' damage mix runs against their lean");
        text.AppendLine();
        text.AppendLine("Bought by at least 25 sampled players (and not by 25), |t| of 3 or more, and the lean the table gives them points the other way. "
                        + "Leads for a lean override, not verdicts: a hundred or so matches say little about any one item.");
        text.AppendLine();
        text.AppendLine(disagree.Count == 0 ? "None." : "| item | lean | buyers | extra bullet share | t |\n|---|---|---|---|---|");
        foreach (var (itemId, buyers, extra, t) in disagree)
            text.AppendLine($"| {itemId} | {store.LeanOf(itemId).Lean:+0.00;-0.00} | {buyers} | {extra:+0.0;-0.0} | {t:0.0} |");
        return text.ToString();
    }

    private static IEnumerable<(string, List<Point>)> Brackets(List<Point> points)
    {
        yield return ("all", points);
        yield return ("spent under 12k", points.Where(point => point.Spent < 12000).ToList());
        yield return ("spent 12k to 24k", points.Where(point => point.Spent is >= 12000 and < 24000).ToList());
        yield return ("spent 24k and over", points.Where(point => point.Spent >= 24000).ToList());
    }

    private static string Slope3((double Slope, double Low, double High) fit) => $"{fit.Slope:0.0} ({fit.Low:0.0} to {fit.High:0.0})";

    [Fact]
    public void TheSlopeIsTheWithinHeroRelationBetweenLeanAndShare()
    {
        // Two heroes with very different mixes, both moving 20 points of bullet share per unit of lean.
        var points = new List<Point>();
        foreach (var (hero, baseShare) in new[] { ("a", 30.0), ("b", 70.0) })
        {
            for (var i = 0; i < 6; i++)
            {
                var lean = (i - 2.5) / 5;
                points.Add(new Point(i + (hero == "a" ? 0 : 100), hero, lean, lean, baseShare + 20 * lean, 100 - baseShare - 20 * lean, 20000, []));
            }
        }

        Assert.Equal(20.0, Slope(points, point => point.BulletShare, point => point.Lean), 9);
        Assert.Equal(-20.0, Slope(points, point => point.SpiritShare, point => point.Lean), 9);
        Assert.Equal(1.0, RSquared(points, point => point.BulletShare, point => point.Lean), 9);
        var (slope, low, high) = WithInterval(points, point => point.BulletShare, point => point.Lean);
        Assert.Equal(20.0, slope, 9);
        Assert.InRange(20.0, low - 1e-9, high + 1e-9);
    }

    [Fact]
    public void AnItemWhoseBuyersRunAboveWhatTheirLeanExplainsIsFound()
    {
        // Twenty-eight players of one hero, a lean that explains the share exactly, except that those who bought "odd" have 10 points more.
        var points = Enumerable.Range(0, 60).Select(i =>
        {
            var lean = (i % 10 - 4.5) / 5;
            var odd = i % 3 == 0;
            return new Point(i, "a", lean, lean, 40 + 20 * lean + (odd ? 10 : 0), 60 - 20 * lean, 20000, odd ? ["odd"] : ["plain"]);
        }).ToList();

        var found = ItemEffects(points, minBuyers: 10);

        var odd = Assert.Single(found, entry => entry.ItemId == "odd");
        Assert.InRange(odd.Extra, 5.0, 12.0);
        Assert.True(Math.Sign(found.Single(entry => entry.ItemId == "plain").Extra) < 0);
    }
}
