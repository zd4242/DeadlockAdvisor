using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Vision;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Tests;

/// <summary>The three bars under a portrait, and what they say against the items read.</summary>
public class BarReaderTests
{
    private const double _pitch = 116.8;
    private const double _top = 29.8;
    private const double _centre = 600;

    [Fact]
    public void AFullBarIsTheTrackHeightAndAnEmptyOneIsNone()
    {
        var image = Board(weapon: 0.27, spirit: 0, vitality: 0.135);
        var bars = BarReader.Read(image, _top, _pitch, _centre)!;
        Assert.InRange(bars.Weapon, 0.27 - 1 / _pitch, 0.27);
        Assert.Equal(0, bars.Spirit);
        Assert.InRange(bars.Vitality, 0.135 - 1 / _pitch, 0.135 + 1 / _pitch);
    }

    [Fact]
    public void ABarCannotBeFullerThanItsTrack()
    {
        var image = Board(weapon: 0.4, spirit: 0.4, vitality: 0.4);
        var bars = BarReader.Read(image, _top, _pitch, _centre)!;
        Assert.Equal(BarReader.TrackHeight, bars.Weapon);
        Assert.Equal(BarReader.TrackHeight, bars.Of(ShopCategory.Spirit));
        Assert.Equal(BarReader.TrackHeight, bars.Of(ShopCategory.Vitality));
    }

    [Fact]
    public void BarsOffTheImageAreNotRead()
    {
        Assert.Null(BarReader.Read(new RgbImage(300, 100), _top, _pitch, _centre));
    }

    [Fact]
    public void TheExpectedFillRisesAndFlattens()
    {
        Assert.Equal(0, BarReader.ExpectedFill(0));
        Assert.InRange(BarReader.ExpectedFill(10_000), 0.63, 0.64);
        Assert.InRange(BarReader.ExpectedFill(1_600), 0.14, 0.16);
        var previous = -1.0;
        for (var souls = 0; souls <= 60_000; souls += 800)
        {
            var fill = BarReader.ExpectedFill(souls);
            Assert.True(fill > previous && fill < 1.0);
            previous = fill;
        }
        Assert.True(BarReader.ExpectedFill(40_000) - BarReader.ExpectedFill(30_000) < BarReader.ExpectedFill(10_000) - BarReader.ExpectedFill(0));
    }

    /// <summary>With every cell's tier known the souls are exact, and a bar has to be near what they give.</summary>
    [Fact]
    public void ABarThatMatchesItsItemsAgrees()
    {
        var templates = ItemData.Templates;
        var hero = Hero([Item(0, ShopCategory.Weapon, 2), Item(1, ShopCategory.Weapon, 2), Item(2, ShopCategory.Spirit, 3), Item(3, ShopCategory.Vitality, 1)],
            weapon: BarReader.ExpectedFill(3200), spirit: BarReader.ExpectedFill(3200) + 0.1, vitality: BarReader.ExpectedFill(800) - 0.1);
        var checks = BarReader.Check(hero, templates);
        Assert.Equal(3, checks.Count);
        Assert.All(checks, check => Assert.True(check.Exact));
        Assert.Equal([3200, 800, 3200], checks.Select(check => check.Souls));
        Assert.Equal([ShopCategory.Weapon, ShopCategory.Vitality, ShopCategory.Spirit], checks.Select(check => check.Category));
        Assert.All(checks, check => Assert.False(check.Disagrees));
    }

    [Fact]
    public void ABarFarFromItsItemsDisagrees()
    {
        var hero = Hero([Item(0, ShopCategory.Weapon, 2), Item(1, ShopCategory.Weapon, 2)],
            weapon: BarReader.ExpectedFill(3200) + 0.3, spirit: 0.4, vitality: 0);
        var checks = BarReader.Check(hero, ItemData.Templates).ToDictionary(check => check.Category);
        Assert.True(checks[ShopCategory.Weapon].Disagrees);
        Assert.True(checks[ShopCategory.Spirit].Disagrees);
        Assert.False(checks[ShopCategory.Vitality].Disagrees);
    }

    /// <summary>A cell of unknown tier or a hidden one may hold more, so the souls are only a least: a bar fuller than that is fine, an emptier one isn't.</summary>
    [Fact]
    public void AnUnknownCellMakesTheSoulsALeast()
    {
        var unknown = new CellRead(2, CellKind.Item, ShopCategory.Weapon, null, 0.5, 0, false);
        var hero = Hero([Item(0, ShopCategory.Weapon, 3), Item(1, ShopCategory.Spirit, 3), unknown],
            weapon: 0.9, spirit: 0.1, vitality: 0);
        var checks = BarReader.Check(hero, ItemData.Templates).ToDictionary(check => check.Category);
        Assert.False(checks[ShopCategory.Weapon].Exact);
        Assert.False(checks[ShopCategory.Weapon].Disagrees);
        Assert.True(checks[ShopCategory.Spirit].Exact);
        Assert.True(checks[ShopCategory.Spirit].Disagrees);

        var hidden = Hero([Item(0, ShopCategory.Weapon, 3), new CellRead(1, CellKind.Hidden, null, null, 0, 0, false)], weapon: 0.05, spirit: 0.5, vitality: 0.5);
        var hiddenChecks = BarReader.Check(hidden, ItemData.Templates);
        Assert.All(hiddenChecks, check => Assert.False(check.Exact));
        Assert.True(hiddenChecks.Single(check => check.Category == ShopCategory.Weapon).Disagrees);
        Assert.False(hiddenChecks.Single(check => check.Category == ShopCategory.Spirit).Disagrees);
    }

    [Fact]
    public void AHeroWithoutBarsHasNothingToCheck()
    {
        Assert.Empty(BarReader.Check(new HeroItems(0, [], null, Covered: false), ItemData.Templates));
    }

    // -- helpers --------------------------------------------------------------------------------

    private static CellRead Item(int cell, ShopCategory category, int tier) => new(cell, CellKind.Item, category, null, 0.5, 0, false, tier);

    private static HeroItems Hero(IReadOnlyList<CellRead> cells, double weapon, double spirit, double vitality) =>
        new(0, cells, new BarsRead(weapon * BarReader.TrackHeight, spirit * BarReader.TrackHeight, vitality * BarReader.TrackHeight), Covered: false);

    /// <summary>A dark panel with the three bars lit from the foot of their track up by the heights given (in pitches).</summary>
    private static RgbImage Board(double weapon, double spirit, double vitality)
    {
        var image = new RgbImage(1200, 600);
        for (var i = 0; i < image.Pixels.Length; i++)
            image.Pixels[i] = 40;
        double[] offsets = [-0.125, 0, 0.135];
        double[] heights = [weapon, spirit, vitality];
        var bottom = (int)Math.Round(_top + 2.44 * _pitch);
        for (var bar = 0; bar < 3; bar++)
        {
            var x = (int)Math.Round(_centre + offsets[bar] * _pitch);
            for (var y = bottom - (int)Math.Round(heights[bar] * _pitch); y < bottom; y++)
            {
                for (var px = x - 3; px <= x + 3; px++)
                    (image.Pixels[(y * image.Width + px) * 3], image.Pixels[(y * image.Width + px) * 3 + 1], image.Pixels[(y * image.Width + px) * 3 + 2]) = (230, 230, 220);
            }
        }
        return image;
    }
}
