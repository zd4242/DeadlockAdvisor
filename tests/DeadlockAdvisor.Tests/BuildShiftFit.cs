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
/// rows.csv already there it measures again without asking for anything, unless DEADLOCK_BUILD_FIT_REFETCH is set. Nothing is committed
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
        if (!File.Exists(csv) || Environment.GetEnvironmentVariable("DEADLOCK_BUILD_FIT_REFETCH") is { Length: > 0 })
            File.WriteAllLines(csv, (await Sample(store, matches)).Select(Format));

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

    private static async Task<List<FitRow>> Sample(DataStore store, int matches)
    {
        using var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("deadlock-advisor/dev (+https://github.com/zd4242/DeadlockAdvisor) (build shift fit)");
        var heroes = store.Heroes.Values.Where(hero => hero.GameId != 0).ToDictionary(hero => hero.GameId, hero => hero.HeroId);
        var random = new Random(29);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = new List<FitRow>();
        var seen = new HashSet<long>();
        var attempts = 0;
        while (seen.Count < matches && attempts++ < matches * 3)
        {
            // Ten minutes at a random time between one and eight days ago, a few matches from each.
            var from = now - 86400 - random.NextInt64(7 * 86400);
            var list = await Get(client, $"{Api}/v1/matches/metadata?limit=3&min_unix_timestamp={from}&max_unix_timestamp={from + 600}"
                                         + $"&min_duration_s={MinDuration}&game_mode=normal");
            foreach (var id in JsonDocument.Parse(list).RootElement.EnumerateArray().Select(entry => entry.GetProperty("match_id").GetInt64()))
            {
                if (!seen.Add(id) || seen.Count > matches)
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

    /// <summary>One player: how far the build leans weapon over spirit from the hero's usual, and the damage mix that came with it.</summary>
    public sealed record Point(long MatchId, string HeroId, double Lean, double BulletShare, double SpiritShare, double Spent);

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
            var souls = Enum.GetValues<ShopCategory>().ToDictionary(category => category, _ => 0.0);
            foreach (var gameId in row.Items.Where(known.ContainsKey))
                souls[ItemTemplates.CategoryOf(known[gameId].Category)!.Value] += HeroBuild.TierCost(known[gameId].Tier);
            var spent = souls.Values.Sum();
            if (spent <= 0)
                continue;
            var lean = (souls[ShopCategory.Weapon] / spent - usual[ShopCategory.Weapon]) - (souls[ShopCategory.Spirit] / spent - usual[ShopCategory.Spirit]);
            points.Add(new Point(row.MatchId, row.HeroId, lean, 100.0 * row.Bullet / total, 100.0 * row.Ability / total, spent));
        }
        return points;
    }

    /// <summary>
    /// The slope of y on x within each hero (both measured from the hero's own average, so what a hero is rated at doesn't count, only how
    /// this player differs from it): Σ x̃ỹ ÷ Σ x̃². Heroes with fewer than <paramref name="minPerHero"/> players are left out.
    /// </summary>
    public static double Slope(IReadOnlyList<Point> points, Func<Point, double> y, int minPerHero = 3)
    {
        var (numerator, denominator) = (0.0, 0.0);
        foreach (var hero in points.GroupBy(point => point.HeroId).Where(group => group.Count() >= minPerHero))
        {
            var (meanX, meanY) = (hero.Average(point => point.Lean), hero.Average(y));
            foreach (var point in hero)
            {
                numerator += (point.Lean - meanX) * (y(point) - meanY);
                denominator += (point.Lean - meanX) * (point.Lean - meanX);
            }
        }
        return denominator == 0 ? 0 : numerator / denominator;
    }

    /// <summary>The slope with a 95% interval from resampling whole matches, since a match's ten players aren't independent.</summary>
    public static (double Slope, double Low, double High) WithInterval(IReadOnlyList<Point> points, Func<Point, double> y, int seed = 1)
    {
        var byMatch = points.GroupBy(point => point.MatchId).Select(group => group.ToList()).ToList();
        var random = new Random(seed);
        var slopes = new List<double>();
        for (var i = 0; i < Bootstraps; i++)
        {
            var resampled = Enumerable.Range(0, byMatch.Count).SelectMany(_ => byMatch[random.Next(byMatch.Count)]).ToList();
            slopes.Add(Slope(resampled, y));
        }
        slopes.Sort();
        return (Slope(points, y), slopes[(int)(0.025 * slopes.Count)], slopes[(int)(0.975 * slopes.Count)]);
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
        text.AppendLine("Slope: points of damage share (a hundred is all of it) per unit of lean, where a lean of 1 is every soul going weapon instead of spirit "
                        + "from an even split; within each hero, 95% interval from resampling matches.");
        text.AppendLine();
        text.AppendLine($"Shipped: Alpha = {BuildShifts.Alpha}, FullRampSouls = {BuildShifts.FullRampSouls}.");
        text.AppendLine();
        text.AppendLine("| | players | bullet share | spirit share (ability) |");
        text.AppendLine("|---|---|---|---|");
        foreach (var (label, subset) in Brackets(points))
        {
            var bullet = WithInterval(subset, point => point.BulletShare);
            var spirit = WithInterval(subset, point => point.SpiritShare);
            text.AppendLine($"| {label} | {subset.Count} | {Slope3(bullet)} | {Slope3(spirit)} |");
        }
        text.AppendLine();
        var lean = points.Select(point => point.Lean).Order().ToList();
        text.AppendLine($"Lean: 5th to 95th percentile {lean[(int)(0.05 * lean.Count)]:0.00} to {lean[(int)(0.95 * lean.Count)]:0.00}; "
                        + $"damage mix of the average player {points.Average(point => point.BulletShare):0.0}% bullet, {points.Average(point => point.SpiritShare):0.0}% ability.");
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
                points.Add(new Point(i + (hero == "a" ? 0 : 100), hero, lean, baseShare + 20 * lean, 100 - baseShare - 20 * lean, 20000));
            }
        }

        Assert.Equal(20.0, Slope(points, point => point.BulletShare), 9);
        Assert.Equal(-20.0, Slope(points, point => point.SpiritShare), 9);
        var (slope, low, high) = WithInterval(points, point => point.BulletShare);
        Assert.Equal(20.0, slope, 9);
        Assert.InRange(20.0, low - 1e-9, high + 1e-9);
    }
}
