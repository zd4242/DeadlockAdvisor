using DeadlockAdvisor.Vision;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Tests;

/// <summary>Where the item cells are, and finding them: synthetic boards with tags drawn where a known lattice puts them.</summary>
public class ItemGridTests
{
    private const int _width = 2560;
    private const int _height = 748;

    private static readonly Geometry _grid = new(1279.2, 116.8, 29.8, 1.67, 0.528);

    [Fact]
    public void CellCentresAreInPitchesFromTheSlotCentreAndTheGridsTop()
    {
        var centers = _grid.Centers();
        var (x0, y0) = ItemGrid.Centre(_grid, centers[2], 0);
        var (x1, y1) = ItemGrid.Centre(_grid, centers[2], 1);
        var (x2, y2) = ItemGrid.Centre(_grid, centers[2], 2);
        Assert.Equal(centers[2] - 0.185 * 116.8, x0, 6);
        Assert.Equal(29.8 + 3.82 * 116.8, y0, 6);
        Assert.Equal(centers[2] + 0.213 * 116.8, x1, 6);
        Assert.Equal(y0, y1, 6);
        Assert.Equal(x0, x2, 6);
        Assert.Equal(y0 + 0.396 * 116.8, y2, 6);
        Assert.Equal(2 * 0.150 * 116.8, ItemGrid.CellSide(_grid), 6);
    }

    /// <summary>The scoreboard capture is the top 52% of the screen, and the deepest of the twelve cells has to be in it at the HUD sizes measured (a pitch 0.0811 to 0.082 of the screen's height, the grid's top at most 0.021).</summary>
    [Theory]
    [InlineData(1440, 0.0811, 0.0207)]
    [InlineData(1080, 0.0811, 0.0207)]
    [InlineData(1437, 0.0816, 0.0120)]
    [InlineData(1440, 0.0820, 0.0210)]
    public void TheDeepestCellIsInTheCapture(int screenHeight, double pitchRatio, double topRatio)
    {
        var pitch = pitchRatio * screenHeight;
        var bottom = topRatio * screenHeight + ItemGrid.Bottom * pitch;
        Assert.True(bottom < 0.52 * screenHeight, $"the deepest cell reaches {bottom / screenHeight:0.000} of the screen");
        Assert.Equal(12, ItemGrid.CellsPerHero);
    }

    [Fact]
    public void ThePlaceMovesAndScalesTheGridsNumbers()
    {
        var centers = _grid.Centers();
        var plain = ItemPlace.Default(_grid, centers);
        Assert.Equal(ItemGrid.Centre(_grid, centers[7], 5), plain.Cell(7, 5));

        var moved = new ItemPlace(_grid, centers, 0.01, -0.02, -0.03, 0.99);
        var (x, y) = moved.Cell(7, 5);
        var pitch = 116.8 * 0.99;
        Assert.Equal(_grid.CenterX + (centers[7] - _grid.CenterX) * 0.99 - 0.02 * 116.8 + 0.213 * pitch, x, 6);
        Assert.Equal(29.8 - 0.03 * 116.8 + (3.82 + 0.396 * 2) * pitch, y, 6);
        Assert.Equal(2 * 0.150 * pitch, moved.Side, 6);
    }

    [Theory]
    [InlineData(0.0, -0.02, 0.0, 1.00)]
    [InlineData(0.012, -0.03, -0.012, 1.00)]
    [InlineData(0.0, -0.03, 0.0, 0.99)]
    [InlineData(-0.025, 0.02, 0.03, 1.01)]
    public void ALatticeIsFoundFromTheTags(double left, double slideY, double right, double scale)
    {
        var centers = _grid.Centers();
        var truth = new ItemPlace(_grid, centers, left, right, slideY, scale);
        var board = Board(truth, slots: [2, 3, 4, 5, 6, 7, 8, 9], cells: 12, ShopCategory.Weapon);

        var (candidates, cells) = ItemPlace.Fit(TagMap.Build(board, 0, (int)ItemPlace.ReachTop(_grid), board.Width, (int)ItemPlace.ReachBottom(_grid)), _grid, centers);
        Assert.Contains(candidates, candidate => Math.Abs(candidate.Scale - scale) < 0.005);
        var found = candidates.First(candidate => Math.Abs(candidate.Scale - scale) < 0.005);

        Assert.True(cells >= 80, $"{cells} cells lined up");
        var shown = $"found left {found.SlideLeft:0.000} right {found.SlideRight:0.000} y {found.SlideY:0.000} scale {found.Scale:0.00}";
        
        Assert.True(Math.Abs(found.SlideY - slideY) <= 0.02, shown);
        Assert.True(Math.Abs(found.SlideLeft - left) <= 0.02, shown);
        Assert.True(Math.Abs(found.SlideRight - right) <= 0.02, shown);
    }

    [Fact]
    public void ABoardWithNoTagsKeepsTheGridsNumbers()
    {
        var empty = new RgbImage(_width, _height);
        var (candidates, cells) = ItemPlace.Fit(TagMap.Build(empty, 0, 0, empty.Width, empty.Height), _grid, _grid.Centers());
        var found = Assert.Single(candidates);
        Assert.Equal(0, cells);
        Assert.Equal((0.0, 0.0, 0.0, 1.0), (found.SlideLeft, found.SlideRight, found.SlideY, found.Scale));
    }

    /// <summary>The corner's colour says the shop category: the three tags measured, on both teams' panels.</summary>
    [Theory]
    [InlineData(232, 133, 15, ShopCategory.Weapon)]
    [InlineData(60, 105, 15, ShopCategory.Vitality)]
    [InlineData(100, 145, 16, ShopCategory.Vitality)]
    [InlineData(145, 84, 190, ShopCategory.Spirit)]
    [InlineData(123, 75, 122, ShopCategory.Spirit)]
    public void ATagsColourIsItsCategory(int r, int g, int b, ShopCategory category)
    {
        var tags = TagMap.Build(Fill(30, 30, (r, g, b)), 0, 0, 30, 30);
        Assert.Equal(1.0, tags.Density(15, 15, 5), 6);
        Assert.Equal(category, tags.Dominant(15, 15, 5).Category);
    }

    /// <summary>The panel behind the tags, brown for one team and blue-grey for the other, however a scene shows through, is no tag.</summary>
    [Theory]
    [InlineData(110, 85, 60)]
    [InlineData(70, 55, 40)]
    [InlineData(55, 62, 88)]
    [InlineData(60, 70, 100)]
    [InlineData(40, 40, 40)]
    [InlineData(200, 190, 175)]
    public void APanelIsNoTag(int r, int g, int b)
    {
        var tags = TagMap.Build(Fill(30, 30, (r, g, b)), 0, 0, 30, 30);
        Assert.Equal(0.0, tags.Density(15, 15, 5), 6);
    }

    /// <summary>A tag has its numeral in dark strokes. A glare of the same colour over the panel has none, and isn't a tag.</summary>
    [Fact]
    public void AGlareWithoutANumeralIsNoTag()
    {
        var centers = _grid.Centers();
        var place = ItemPlace.Default(_grid, centers);
        var real = Board(place, slots: [4], cells: 12, ShopCategory.Weapon);
        var tags = TagMap.Build(real, 0, (int)ItemPlace.ReachTop(_grid), real.Width, (int)ItemPlace.ReachBottom(_grid));
        Assert.True(place.Tag(tags, 4, 3).IsTag);

        var glare = Board(place, slots: [4], cells: 12, ShopCategory.Weapon, numerals: false);
        var glareTags = TagMap.Build(glare, 0, (int)ItemPlace.ReachTop(_grid), glare.Width, (int)ItemPlace.ReachBottom(_grid));
        var (isTag, density) = place.Tag(glareTags, 4, 3);
        Assert.False(isTag);
        Assert.True(density > ItemPlace.TagDensity);
        Assert.True(place.Flooded(glareTags, 4) >= ItemPlace.FloodedCovers);
        Assert.Equal(0, place.Flooded(tags, 4));
    }

    // -- drawing -------------------------------------------------------------------------------

    private static RgbImage Fill(int width, int height, (int R, int G, int B) colour)
    {
        var image = new RgbImage(width, height);
        for (var i = 0; i < width * height; i++)
            (image.Pixels[i * 3], image.Pixels[i * 3 + 1], image.Pixels[i * 3 + 2]) = ((byte)colour.R, (byte)colour.G, (byte)colour.B);
        return image;
    }

    /// <summary>A dark panel with a tag in the top right corner of the first <paramref name="cells"/> cells of each slot, as <paramref name="place"/> puts them.</summary>
    internal static RgbImage Board(ItemPlace place, int[] slots, int cells, ShopCategory category, bool numerals = true)
    {
        var image = Fill(_width, _height, (60, 48, 36));
        var tag = category switch
        {
            ShopCategory.Weapon => (232, 133, 15),
            ShopCategory.Vitality => (60, 105, 15),
            _ => (145, 84, 190),
        };
        foreach (var slot in slots)
        {
            for (var cell = 0; cell < cells; cell++)
            {
                var (x, y) = place.Cell(slot, cell);
                var side = place.Side;
                for (var py = (int)(y - side / 2); py < (int)(y - side / 2 + 0.5 * side); py++)
                {
                    for (var px = (int)(x + side / 2 - 0.5 * side); px < (int)(x + side / 2); px++)
                    {
                        double u = x + side / 2 - (px + 0.5), v = py + 0.5 - (y - side / 2);
                        if (u + v <= 0.47 * side && px >= 0 && py >= 0 && px < image.Width && py < image.Height)
                            Set(image, px, py, tag);
                    }
                }
                if (!numerals)
                    continue;
                // The numeral: a couple of dark bars inside the tag, about where the game draws them.
                for (var bar = 0; bar < 2; bar++)
                {
                    var barX = (int)Math.Round(x + side / 2 - 0.19 * side + bar * 0.07 * side);
                    for (var py = (int)Math.Round(y - side / 2 + 0.07 * side); py < (int)Math.Round(y - side / 2 + 0.2 * side); py++)
                    {
                        for (var px = barX; px < barX + Math.Max(1, (int)Math.Round(0.03 * side)); px++)
                            Set(image, px, py, (15, 10, 5));
                    }
                }
            }
        }
        return image;
    }

    private static void Set(RgbImage image, int x, int y, (int R, int G, int B) colour)
    {
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height)
            return;
        (image.Pixels[(y * image.Width + x) * 3], image.Pixels[(y * image.Width + x) * 3 + 1], image.Pixels[(y * image.Width + x) * 3 + 2]) = ((byte)colour.R, (byte)colour.G, (byte)colour.B);
    }
}
