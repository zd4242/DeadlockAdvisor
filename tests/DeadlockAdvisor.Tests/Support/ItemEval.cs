using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Vision;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Tests.Support;

/// <summary>A labelled board read at one size: the board's image shrunk by <see cref="Scale"/> (1 is as captured), then read.</summary>
public sealed record ItemOutcome(ItemCorpusBoard Board, double Scale, ItemBoardRead Read)
{
    public int Height => (int)Math.Round(Board.Label.ScreenHeight * Scale);

    public string Name => $"{Board.Name}@{Height}";

    public IEnumerable<HeroItems> Heroes => Read.Heroes;
}

/// <summary>Where a read differs from its label, by "slot:cell" (or by slot).</summary>
public sealed class ItemTally
{
    public List<string> ConfidentCorrect { get; } = [];
    public List<string> ConfidentWrong { get; } = [];
    public List<string> Unverified { get; } = [];
    public List<string> Unknown { get; } = [];
    public List<string> CategoryWrong { get; } = [];
    public List<string> KindWrong { get; } = [];
    public List<string> TierWrong { get; } = [];
    /// <summary>"slot:category" of the bars that disagree with the items read (not checked on a Street Brawl).</summary>
    public List<string> BarDisagree { get; } = [];

    public List<int> CountWrong { get; } = [];
    public List<int> CoverWrong { get; } = [];

    /// <summary>The cells of items a label and a read both have, which the rest are shares of.</summary>
    public int Cells { get; set; }
}

/// <summary>
/// The item reader measured against labelled boards: counts, categories, identities, tiers, covered slots, and the bars against
/// the items. A confident read of a cell the label doesn't name is "unverified": it may be right, but nobody has looked.
/// </summary>
public static class ItemEval
{
    /// <summary>The sizes read: as captured, and shrunk to what a 1080p and a 900p screen draw (simulated, from the 1440p captures).</summary>
    public static readonly double[] Scales = [1.0, 0.75, 0.625];

    public static List<ItemOutcome> Run(IEnumerable<ItemCorpusBoard> boards, ItemTemplates templates, double[]? scales = null) =>
        boards.SelectMany(board => (scales ?? Scales).Select(scale => Run(board, scale, templates))).ToList();

    public static ItemOutcome Run(ItemCorpusBoard board, double scale, ItemTemplates templates)
    {
        var (image, grid) = Scaled(board, scale);
        return new ItemOutcome(board, scale, ItemReader.Read(image, grid, templates, board.Label.BlankSlots));
    }

    public static (RgbImage Image, Geometry Grid) Scaled(ItemCorpusBoard board, double scale)
    {
        var grid = board.Label.Grid;
        if (scale == 1.0)
            return (board.Image, grid);
        var image = board.Image.Resize((int)Math.Round(board.Image.Width * scale), (int)Math.Round(board.Image.Height * scale), ResampleFilter.Bilinear);
        return (image, grid with { CenterX = grid.CenterX * scale, Pitch = grid.Pitch * scale, Top = grid.Top * scale });
    }

    public static ItemTally Tally(ItemOutcome outcome, ItemTemplates templates)
    {
        var tally = new ItemTally();
        foreach (var hero in outcome.Heroes)
        {
            var label = outcome.Board.Label.HeroAt(hero.Slot);
            if (label.Covered != hero.Covered)
                tally.CoverWrong.Add(hero.Slot);
            if (hero.Covered)
                continue;
            if (outcome.Board.Label.BlankSlots.Count == 0)
                tally.BarDisagree.AddRange(BarReader.Check(hero, templates).Where(check => check.Disagrees).Select(check => $"{hero.Slot}:{check.Category.ToString().ToLowerInvariant()}"));
            if (hero.Count != label.Cells.Count)
                tally.CountWrong.Add(hero.Slot);
            for (var i = 0; i < Math.Min(hero.Count, label.Cells.Count); i++)
            {
                var (kind, category, id, labelledTier) = HeroLabel.Parse(label.Cells[i]);
                var read = hero.Cells[i];
                var where = $"{hero.Slot}:{i}";
                if (read.Kind.ToString().ToLowerInvariant() != kind)
                {
                    tally.KindWrong.Add(where);
                    continue;
                }
                if (kind != "item")
                    continue;
                tally.Cells++;
                if (read.Category != category)
                    tally.CategoryWrong.Add(where);
                var truthTier = id is not null && templates.IndexOf(id) is { } index ? templates.Items[index].Tier : labelledTier;
                if (read.Tier is { } tier && truthTier is { } wanted && tier != wanted)
                    tally.TierWrong.Add(where);
                if (!read.IsIdentified)
                    tally.Unknown.Add(where);
                else if (id is null)
                    tally.Unverified.Add($"{where} ({read.ItemId})");
                else if (id == read.ItemId)
                    tally.ConfidentCorrect.Add(where);
                else
                    tally.ConfidentWrong.Add($"{where} ({read.ItemId}, not {id})");
            }
        }
        return tally;
    }

    /// <summary>What the corpus test pins: per board and size, which cells were read confidently right, and where anything was wrong.</summary>
    public static JsonObject Pinned(IReadOnlyList<ItemOutcome> outcomes, ItemTemplates templates)
    {
        var captures = new JsonObject();
        foreach (var outcome in outcomes)
        {
            var tally = Tally(outcome, templates);
            captures[outcome.Name] = new JsonObject
            {
                ["confident_correct"] = Strings(tally.ConfidentCorrect),
                ["confident_wrong"] = Strings(tally.ConfidentWrong),
                ["unverified"] = Strings(tally.Unverified),
                ["category_wrong"] = Strings(tally.CategoryWrong),
                ["kind_wrong"] = Strings(tally.KindWrong),
                ["tier_wrong"] = Strings(tally.TierWrong),
                ["bar_disagree"] = Strings(tally.BarDisagree),
                ["count_wrong"] = new JsonArray(tally.CountWrong.Select(slot => (JsonNode?)slot).ToArray()),
                ["cover_wrong"] = new JsonArray(tally.CoverWrong.Select(slot => (JsonNode?)slot).ToArray()),
            };
        }
        return new JsonObject { ["captures"] = captures };
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)value).ToArray());

    public static string Markdown(IReadOnlyList<ItemOutcome> outcomes, ItemTemplates templates)
    {
        var report = new StringBuilder("# The item reader on the labelled boards\n\n");
        report.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{outcomes.Select(o => o.Board.Name).Distinct().Count()} boards, each read as captured and shrunk to what 1080p and 900p screens draw (simulated). Every confident read is checked against its label; an \"unverified\" one is a cell the label doesn't name.\n"));
        report.AppendLine("| size | cells | confident right | confident wrong | unverified | unknown | category wrong | tier wrong | heroes' counts right | slots covered right | bars disagreeing |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var size in outcomes.GroupBy(outcome => outcome.Height).OrderByDescending(group => group.Key))
        {
            var tallies = size.Select(outcome => (outcome, tally: Tally(outcome, templates))).ToList();
            var cells = tallies.Sum(t => t.tally.Cells);
            int Sum(Func<ItemTally, int> count) => tallies.Sum(t => count(t.tally));
            var heroes = size.Sum(outcome => outcome.Heroes.Count(hero => !hero.Covered));
            var checks = size.Where(outcome => outcome.Board.Label.BlankSlots.Count == 0)
                .SelectMany(outcome => outcome.Heroes.SelectMany(hero => hero.Covered ? [] : BarReader.Check(hero, templates))).ToList();
            var covered = size.Sum(outcome => outcome.Heroes.Count(hero => hero.Covered));
            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {size.Key}p | {cells} | {Share(Sum(t => t.ConfidentCorrect.Count), cells)} | {Sum(t => t.ConfidentWrong.Count)} | {Sum(t => t.Unverified.Count)} | {Share(Sum(t => t.Unknown.Count), cells)} | {Sum(t => t.CategoryWrong.Count)} | {Sum(t => t.TierWrong.Count)} | {heroes - Sum(t => t.CountWrong.Count)} of {heroes} | {covered} read as covered, {Sum(t => t.CoverWrong.Count)} wrong | {checks.Count(check => check.Disagrees)} of {checks.Count} ({checks.Count(check => check.Exact)} exact) |"));
        }

        report.AppendLine("\n## Per board\n");
        report.AppendLine("| board | confident right | unknown | wrong in any way |");
        report.AppendLine("|---|---|---|---|");
        foreach (var outcome in outcomes.Where(outcome => outcome.Scale == 1.0))
        {
            var tally = Tally(outcome, templates);
            var wrong = tally.ConfidentWrong.Count + tally.CategoryWrong.Count + tally.KindWrong.Count + tally.TierWrong.Count + tally.CountWrong.Count + tally.CoverWrong.Count;
            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {outcome.Board.Name} ({outcome.Board.Label.Phase}) | {Share(tally.ConfidentCorrect.Count, tally.Cells)} | {tally.Unknown.Count} | {wrong} |"));
        }
        return report.ToString();
    }

    private static string Share(int part, int whole) => whole == 0 ? "-" : string.Create(CultureInfo.InvariantCulture, $"{part} ({100.0 * part / whole:0}%)");
}
