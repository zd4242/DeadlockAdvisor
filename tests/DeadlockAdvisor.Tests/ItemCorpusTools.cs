using System.Globalization;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Tests.Ui;
using DeadlockAdvisor.Vision;
using DeadlockAdvisor.Vision.Items;
using SkiaSharp;

namespace DeadlockAdvisor.Tests;

/// <summary>
/// Turning kept scoreboard captures into labelled corpus boards. Not tests: each does nothing unless its environment variable
/// points it at a folder. A board is checked by eye on the sheets before its label goes into Golden/vision/items/corpus.
/// </summary>
public class ItemCorpusTools
{
    public static string DraftsDir => Path.Combine(UiHarness.RepoRoot(), "mockups", "item_drafts");

    /// <summary>The icon size the corpus keeps.</summary>
    private const int _artSize = 100;

    /// <summary>The rows above this (in pitches, from the grid's top) are painted out: names, kills and the pills are there.</summary>
    internal const double KeepFrom = 2.05;

    /// <summary>...and the rows below this, which is under the deepest cell.</summary>
    internal const double KeepTo = 6.15;

    /// <summary>A hero's column, from its centre, in pitches: the panel is a little under a pitch wide.</summary>
    internal const double KeepHalfWidth = 0.5;

    /// <summary>
    /// DEADLOCK_ITEM_ART=&lt;an assets/items folder&gt;: every icon in it, shrunk to 100 pixels, into Golden/vision/items/art. Valve's
    /// art, as the hero portraits in Golden/vision/topbar are.
    /// </summary>
    [Fact]
    public void CopyTheItemArt()
    {
        if (Environment.GetEnvironmentVariable("DEADLOCK_ITEM_ART") is not { Length: > 0 } folder)
            return;
        var target = Golden.SourcePathOf("vision", "items", "art");
        Directory.CreateDirectory(target);
        foreach (var path in Directory.GetFiles(folder, "*.png"))
        {
            using var source = SKBitmap.Decode(path);
            using var small = source.Resize(new SKImageInfo(_artSize, _artSize, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKFilterQuality.High);
            using var image = SKImage.FromBitmap(small);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(target, Path.GetFileName(path)), data.ToArray());
        }
    }

    /// <summary>
    /// DEADLOCK_ITEM_DRAFT=&lt;a folder of kept scoreboard captures, scoreboard_*.png beside its .json&gt;: for each, a draft label (the
    /// reader's own, items named only where it was sure) and two sheets in mockups/item_drafts to check it by: the confident reads
    /// (the cell beside the icon it was matched to) and the others (the cell beside its three best candidates). DEADLOCK_ASSETS
    /// reads the full-size art instead of the corpus's.
    /// </summary>
    [Fact]
    public void DraftLabelsForBoards()
    {
        if (Environment.GetEnvironmentVariable("DEADLOCK_ITEM_DRAFT") is not { Length: > 0 } folder)
            return;
        Directory.CreateDirectory(DraftsDir);
        var templates = UiHarness.ArtFolder is { } assets
            ? ItemTemplates.Load(Path.Combine(assets, "items"), Golden.LoadStore().Items.Values)
            : ItemData.Templates;
        foreach (var path in Directory.GetFiles(folder, "*.png").Order(StringComparer.Ordinal))
        {
            var note = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")))!;
            var grid = Geometry.Parse(note["grid"]!.AsObject())!;
            var image = ImageFile.Load(path);
            var name = Path.GetFileNameWithoutExtension(path).Replace("scoreboard_", "");
            var heroes = (JsonArray)note["heroes"]!;
            var blank = new[] { 0, 1, 10, 11 }.Count(slot => heroes[slot] is null) >= 3 ? new List<int> { 0, 1, 10, 11 } : [];
            var read = ItemReader.Read(image, grid, templates, blank);

            var label = new ItemBoardLabel
            {
                ScreenWidth = (int)note["screen_width"]!,
                ScreenHeight = (int)note["screen_height"]!,
                Grid = grid,
                BlankSlots = blank,
                Heroes = read.Heroes.Where(hero => hero.Count > 0 || hero.Covered)
                    .ToDictionary(hero => hero.Slot, hero => new HeroLabel(hero.Cells.Select(HeroLabel.Format).ToList(), hero.Covered)),
            };
            var json = label.ToJson();
            json["source"] = Path.GetFileName(path);
            File.WriteAllText(Path.Combine(DraftsDir, name + ".json"), json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");

            var place = ItemPlace.Default(grid, grid.Centers()) with
            {
                SlideLeft = read.Lattice.SlideLeft, SlideRight = read.Lattice.SlideRight, SlideY = read.Lattice.SlideY, Scale = read.Lattice.Scale,
            };
            var sure = new List<ContactSheet.Tile>();
            var unsure = new List<ContactSheet.Tile>();
            foreach (var hero in read.Heroes)
            {
                foreach (var cell in hero.Cells.Where(cell => cell.Kind == CellKind.Item))
                {
                    var (x, y) = place.Cell(hero.Slot, cell.Cell);
                    var crop = ItemFrame.Sample(image, x, y, place.Side, 64);
                    var where = $"{hero.Slot}:{cell.Cell}";
                    if (cell.ItemId is { } id)
                    {
                        sure.Add(new ContactSheet.Tile([crop, templates.Icons.First(icon => icon.ItemId == id).Art], [where, id]));
                        continue;
                    }
                    var scores = ItemReader.ScoreCell(image, place, templates, templates.FramesAt((int)Math.Round(place.Side)), hero.Slot, cell.Cell, cell.Category!.Value);
                    var best = Enumerable.Range(0, scores.Length).OrderByDescending(i => scores[i]).Take(3).ToList();
                    unsure.Add(new ContactSheet.Tile([crop, .. best.Select(i => (RgbImage?)templates.Items[i].Art)],
                        [where, .. best.Select(i => string.Create(CultureInfo.InvariantCulture, $"{templates.Items[i].ItemId} {scores[i]:0.00}"))]));
                }
            }
            ContactSheet.Save(sure, Path.Combine(DraftsDir, name + "_confident.png"), perRow: 8, size: 64);
            ContactSheet.Save(unsure, Path.Combine(DraftsDir, name + "_other.png"), perRow: 4, size: 64);
        }
    }

    /// <summary>
    /// DEADLOCK_ITEM_PROMOTE=&lt;the folder of kept captures&gt;: every label in Golden/vision/items/corpus without its image yet gets
    /// the capture its "source" names, with everything but the heroes' columns below their bars painted black (so no name, kill
    /// feed or nameplate is in it). The grid is the label's.
    /// </summary>
    [Fact]
    public void PromoteLabelledBoards()
    {
        if (Environment.GetEnvironmentVariable("DEADLOCK_ITEM_PROMOTE") is not { Length: > 0 } folder)
            return;
        foreach (var labels in Directory.GetFiles(Golden.SourcePathOf("vision", "items", "corpus"), "*.json"))
        {
            var target = Path.ChangeExtension(labels, ".png");
            if (File.Exists(target))
                continue;
            var json = JsonNode.Parse(File.ReadAllText(labels))!;
            var image = ImageFile.Load(Path.Combine(folder, (string)json["source"]!));
            Png.Save(Masked(image, ItemBoardLabel.FromJson(json).Grid), target);
        }
    }

    /// <summary>The board with only the heroes' columns from their bars down kept.</summary>
    internal static RgbImage Masked(RgbImage image, Geometry grid)
    {
        var kept = new RgbImage(image.Width, image.Height);
        var (top, bottom) = ((int)Math.Round(grid.Top + KeepFrom * grid.Pitch), Math.Min(image.Height, (int)Math.Round(grid.Top + KeepTo * grid.Pitch)));
        foreach (var center in grid.Centers())
        {
            var (left, right) = (Math.Max(0, (int)Math.Round(center - KeepHalfWidth * grid.Pitch)), Math.Min(image.Width, (int)Math.Round(center + KeepHalfWidth * grid.Pitch)));
            for (var y = top; y < bottom; y++)
                Array.Copy(image.Pixels, (y * image.Width + left) * 3, kept.Pixels, (y * image.Width + left) * 3, (right - left) * 3);
        }
        return kept;
    }
}
