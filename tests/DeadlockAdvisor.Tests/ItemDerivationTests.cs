using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Tests.Ui;
using DeadlockAdvisor.Vision;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Tests;

/// <summary>How an item's icon is framed to be compared with a cell, and that the game frames every icon that way.</summary>
public class ItemDerivationTests
{
    private const int _cellPixels = 35;

    [Fact]
    public void EveryItemWithArtHasAnIconAndACategory()
    {
        var templates = ItemData.Templates;
        var items = Golden.LoadStore().Items.Values.ToList();
        var art = Directory.GetFiles(ItemData.ArtDir, "*.png").Select(Path.GetFileNameWithoutExtension).ToHashSet();
        Assert.Equal(items.Count(item => art.Contains(item.ItemId)), templates.Items.Count);
        Assert.Equal(templates.Items.Count, templates.Icons.Count);
        Assert.All(items, item => Assert.NotNull(ItemTemplates.CategoryOf(item.Category)));
        Assert.All(templates.Items, icon => Assert.NotNull(templates.IndexOf(icon.ItemId)));
    }

    /// <summary>A frame is a vector for a dot product: unit length, with the same length for every icon.</summary>
    [Fact]
    public void EveryIconIsFramedAsAUnitVector()
    {
        var frames = ItemData.Templates.FramesAt(_cellPixels);
        Assert.Equal(ItemData.Templates.Icons.Count, frames.Length);
        Assert.Single(frames.Select(frame => frame.Length).Distinct());
        Assert.All(frames, frame => Assert.InRange(TemplateBank.Dot(frame, frame), 0.999, 1.001));
        Assert.Same(frames, ItemData.Templates.FramesAt(_cellPixels));
    }

    /// <summary>Two items framed alike couldn't be told apart, whatever the cell: each icon has to be its own.</summary>
    [Fact]
    public void NoTwoItemsAreFramedAlike()
    {
        var templates = ItemData.Templates;
        var frames = templates.FramesAt(_cellPixels);
        var closest = (Score: -1f, A: "", B: "");
        for (var a = 0; a < frames.Length; a++)
        {
            for (var b = a + 1; b < frames.Length; b++)
            {
                var score = TemplateBank.Dot(frames[a], frames[b]);
                if (score > closest.Score)
                    closest = (score, templates.Icons[a].ItemId, templates.Icons[b].ItemId);
            }
        }
        Assert.True(closest.Score < 0.95, $"{closest.A} and {closest.B} are framed alike ({closest.Score:0.00})");
    }

    /// <summary>The tag the game draws over an icon's top right, and the strip "ACTIVE" is printed on, are no part of the frame.</summary>
    [Fact]
    public void TheTagAndTheActiveStripAreLeftOutOfTheFrame()
    {
        var random = new Random(26);
        var size = ItemFrame.Size;
        var plain = new RgbImage(size, size);
        random.NextBytes(plain.Pixels);
        var marked = new RgbImage(size, size, (byte[])plain.Pixels.Clone());
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double xf = (x + 0.5) / size, yf = (y + 0.5) / size;
                var tag = xf + (1 - yf) >= 1.6;
                var strip = yf > 0.85 && xf is > 0.15 and < 0.85;
                if (tag || strip)
                    (marked.Pixels[(y * size + x) * 3], marked.Pixels[(y * size + x) * 3 + 1], marked.Pixels[(y * size + x) * 3 + 2]) = (200, 40, 20);
            }
        }
        Assert.InRange(TemplateBank.Dot(ItemFrame.Normalise(plain), ItemFrame.Normalise(marked)), 0.9999, 1.0001);
    }

    /// <summary>The framing is the version's: a change to it (or to the art it frames) changes this fingerprint, and means <see cref="ItemTemplates.Version"/> goes up.</summary>
    [Fact]
    public void TheFramingMatchesItsVersion()
    {
        var frames = ItemData.Templates.FramesAt(_cellPixels);
        var text = new StringBuilder();
        foreach (var frame in frames)
        {
            foreach (var value in frame.Where((_, i) => i % 17 == 0))
                text.Append(value.ToString("0.000", CultureInfo.InvariantCulture)).Append(',');
        }
        var fingerprint = $"version {ItemTemplates.Version}: {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16]}";
        var golden = Golden.SourcePathOf("vision", "items", "frames.txt");
        if (Golden.Updating)
        {
            File.WriteAllText(golden, fingerprint + "\n");
            File.WriteAllText(Golden.PathOf("vision", "items", "frames.txt"), fingerprint + "\n");
            return;
        }
        Assert.Equal(File.ReadAllText(Golden.PathOf("vision", "items", "frames.txt")).Trim(), fingerprint);
    }

    /// <summary>Every item costs 800 doubled at each tier, which is what lets a tier numeral stand for a cost.</summary>
    [Fact]
    public void AnItemCostsByItsTier()
    {
        var items = Golden.LoadStore().Items.Values.ToList();
        Assert.All(items, item => Assert.Equal(800 << (item.Tier - 1), item.Cost));
        var templates = ItemData.Templates;
        foreach (var category in Enum.GetValues<ShopCategory>())
        {
            for (var tier = 1; tier <= 4; tier++)
                Assert.Equal(800 << (tier - 1), templates.CostOf(category, tier));
        }
        Assert.Null(templates.CostOf(ShopCategory.Weapon, 5));
    }

    /// <summary>
    /// The game draws every icon in its cell the same way, so the icons are framed one way: measured afresh off every cell the corpus
    /// reads confidently (listed in mockups/item_frames.md), each slid and scaled to where its icon fits best. If Valve ever frames
    /// them differently, the icons stop sitting where the reader looks for them, and this says so.
    /// </summary>
    [Fact]
    public void TheGameFramesEveryIconTheSameWay()
    {
        var templates = ItemData.Templates;
        var frames = templates.FramesAt(_cellPixels);
        var fits = new List<(string Board, int Slot, int Cell, double Dx, double Dy, double Scale)>();
        foreach (var board in ItemCorpusBoard.LoadAll(ItemData.CorpusDir))
        {
            var read = ItemReader.Read(board.Image, board.Label.Grid, templates, board.Label.BlankSlots);
            var grid = board.Label.Grid;
            var lattice = read.Lattice;
            var place = ItemPlace.Default(grid, grid.Centers()) with { SlideLeft = lattice.SlideLeft, SlideRight = lattice.SlideRight, SlideY = lattice.SlideY, Scale = lattice.Scale };
            var cellFrames = templates.FramesAt((int)Math.Round(place.Side));
            foreach (var hero in read.Heroes)
            {
                foreach (var cell in hero.Cells.Where(cell => cell.IsIdentified).Where((_, i) => i % 3 == 0))
                {
                    var icon = Enumerable.Range(0, templates.Icons.Count).First(i => templates.Icons[i].ItemId == cell.ItemId);
                    var (x, y) = place.Cell(hero.Slot, cell.Cell);
                    (double Score, double Dx, double Dy, double Scale) best = (-1, 0, 0, 1);
                    foreach (var scale in new[] { 0.94, 0.97, 1.0, 1.03, 1.06 })
                    {
                        foreach (var dy in new[] { -0.04, -0.02, 0.0, 0.02, 0.04 })
                        {
                            foreach (var dx in new[] { -0.04, -0.02, 0.0, 0.02, 0.04 })
                            {
                                var vector = ItemFrame.Cell(board.Image, x + dx * place.Side, y + dy * place.Side, place.Side * scale);
                                var score = TemplateBank.Dot(vector, cellFrames[icon]);
                                if (score > best.Score)
                                    best = (score, dx, dy, scale);
                            }
                        }
                    }
                    fits.Add((board.Name, hero.Slot, cell.Cell, best.Dx, best.Dy, best.Scale));
                }
            }
        }

        var report = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"# Where each icon sits in its cell: {fits.Count} cells\n\nOffset from where the reader looks (shares of a cell's side) and scale of the cell it was sampled at, at the best fit.\n\n"));
        double Median(IEnumerable<double> values) { var sorted = values.Order().ToList(); return sorted[sorted.Count / 2]; }
        report.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"median dx {Median(fits.Select(f => f.Dx)):+0.000;-0.000}, dy {Median(fits.Select(f => f.Dy)):+0.000;-0.000}, scale {Median(fits.Select(f => f.Scale)):0.00}"));
        foreach (var group in fits.GroupBy(f => f.Board))
            report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- {group.Key}: {group.Count()} cells, dx {Median(group.Select(f => f.Dx)):+0.000;-0.000}, dy {Median(group.Select(f => f.Dy)):+0.000;-0.000}, scale {Median(group.Select(f => f.Scale)):0.00}"));
        File.WriteAllText(UiHarness.MockupPath("item_frames.md"), report.ToString());

        Assert.True(fits.Count >= 200, $"only {fits.Count} cells measured");
        Assert.True(Math.Abs(Median(fits.Select(f => f.Dx))) <= 0.02 && Math.Abs(Median(fits.Select(f => f.Dy))) <= 0.02 && Median(fits.Select(f => f.Scale)) == 1.0,
            "the icons are not framed where the reader looks for them");
        var steady = fits.Count(f => Math.Abs(f.Dx) <= 0.02 && Math.Abs(f.Dy) <= 0.02 && f.Scale is >= 0.97 and <= 1.03);
        Assert.True(steady >= 0.8 * fits.Count, $"only {steady} of {fits.Count} cells have their icon where the rest do");
        _ = frames;
    }
}
