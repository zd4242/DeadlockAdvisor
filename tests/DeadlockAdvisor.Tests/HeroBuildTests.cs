using DeadlockAdvisor.Models;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Tests;

/// <summary>What the match keeps of a hero's items: a read adds to it, and what it couldn't see is not "nothing".</summary>
public class HeroBuildTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 12, 20, 0, 0, TimeSpan.Zero);

    private static CellRead Item(int cell, ShopCategory category, string? id, int? tier) =>
        new(cell, CellKind.Item, category, id, id is null ? 0 : 0.9, id is null ? 0 : 0.2, id is not null, tier);

    private static CellRead Hidden(int cell) => new(cell, CellKind.Hidden, null, null, 0, 0, false);

    private static CellRead Unseen(int cell) => new(cell, CellKind.Unseen, null, null, 0, 0, false);

    private static HeroItems Read(params CellRead[] cells) => new(0, cells, new BarsRead(0.1, 0.05, 0.02), false);

    private static HeroBuild Build(params CellRead[] cells) => HeroBuild.Merge(null, Read(cells), _t0)!;

    [Fact]
    public void AFirstReadIsKeptAsItWas()
    {
        var build = Build(Item(0, ShopCategory.Weapon, "active_reload", 1), Item(1, ShopCategory.Spirit, null, 2));

        Assert.Equal(["active_reload"], build.ItemIds);
        Assert.Equal(_t0, build.At);
        Assert.Equal(2, build.Cells.Count);
        Assert.Null(build.Cells[1].ItemId);
        Assert.Equal(2, build.Cells[1].Tier);
    }

    [Fact]
    public void ACoveredSlotChangesNothing()
    {
        var known = Build(Item(0, ShopCategory.Weapon, "active_reload", 1));

        var merged = HeroBuild.Merge(known, new HeroItems(0, [], null, Covered: true), _t0.AddMinutes(1));

        Assert.Same(known, merged);
        Assert.Equal(_t0, merged!.At);
    }

    [Fact]
    public void AReadOfNothingAtAllChangesNothingEither()
    {
        var known = Build(Item(0, ShopCategory.Weapon, "active_reload", 1));

        Assert.Same(known, HeroBuild.Merge(known, new HeroItems(0, [], null, Covered: false), _t0.AddMinutes(1)));
        Assert.Null(HeroBuild.Merge(null, new HeroItems(0, [], null, Covered: true), _t0));
    }

    [Fact]
    public void AShortReadKeepsTheCellsItDidNotReach()
    {
        var known = Build(Item(0, ShopCategory.Weapon, "active_reload", 1), Item(1, ShopCategory.Vitality, "extra_health", 1),
            Item(2, ShopCategory.Spirit, "extra_spirit", 1));

        var merged = HeroBuild.Merge(known, Read(Item(0, ShopCategory.Weapon, "active_reload", 1)), _t0.AddMinutes(2))!;

        Assert.Equal(["active_reload", "extra_health", "extra_spirit"], merged.ItemIds);
        Assert.Equal(_t0.AddMinutes(2), merged.At);
    }

    [Fact]
    public void ACellSeenAsAnotherItemTakesTheNewOne()
    {
        var known = Build(Item(0, ShopCategory.Weapon, "active_reload", 1));

        var merged = HeroBuild.Merge(known, Read(Item(0, ShopCategory.Weapon, "fleetfoot", 1)), _t0.AddMinutes(1))!;

        Assert.Equal(["fleetfoot"], merged.ItemIds);
    }

    [Fact]
    public void AnUnsureReadOfACellKeepsTheItemItWasKnownAs()
    {
        var known = Build(Item(0, ShopCategory.Weapon, "active_reload", 2));

        var same = HeroBuild.Merge(known, Read(Item(0, ShopCategory.Weapon, null, 2)), _t0.AddMinutes(1))!;
        var upgraded = HeroBuild.Merge(known, Read(Item(0, ShopCategory.Weapon, null, 3)), _t0.AddMinutes(1))!;

        Assert.Equal(["active_reload"], same.ItemIds);
        Assert.Empty(upgraded.ItemIds);
        Assert.Equal(3, upgraded.Cells[0].Tier);
    }

    [Fact]
    public void AHiddenOrCoveredCellSaysNothingAboutWhatWasThere()
    {
        var known = Build(Item(0, ShopCategory.Weapon, "active_reload", 1), Item(1, ShopCategory.Vitality, "extra_health", 1));

        var merged = HeroBuild.Merge(known, Read(Hidden(0), Unseen(1), Hidden(2)), _t0.AddMinutes(1))!;

        Assert.Equal(["active_reload", "extra_health"], merged.ItemIds);
        Assert.Equal(CellKind.Hidden, merged.Cells[2].Kind);
    }

    [Fact]
    public void AnItemIsHeldOnceSoTheCellItMovedFromLosesIt()
    {
        var known = Build(Item(0, ShopCategory.Weapon, "active_reload", 1), Item(1, ShopCategory.Vitality, "extra_health", 1));

        var merged = HeroBuild.Merge(known, Read(Item(0, ShopCategory.Vitality, "extra_health", 1)), _t0.AddMinutes(1))!;

        Assert.Equal(["extra_health"], merged.ItemIds);
        Assert.Equal(ShopCategory.Vitality, merged.Cells[1].Category);
        Assert.Null(merged.Cells[1].ItemId);
    }

    [Fact]
    public void BarsAreKeptWhenALaterReadCannotReadThem()
    {
        var known = Build(Item(0, ShopCategory.Weapon, "active_reload", 1));

        var merged = HeroBuild.Merge(known, new HeroItems(0, [Item(0, ShopCategory.Weapon, "active_reload", 1)], null, false), _t0.AddMinutes(1))!;

        Assert.Equal(known.Bars, merged.Bars);
    }

    [Fact]
    public void SoulsAreEachCellAtItsTierAndSharesAreTheirSplit()
    {
        // 800 + 1,600 weapon, 3,200 vitality, nothing in spirit.
        var build = Build(Item(0, ShopCategory.Weapon, "a", 1), Item(1, ShopCategory.Weapon, "b", 2), Item(2, ShopCategory.Vitality, "c", 3));

        Assert.Equal(new BuildInvestment(ShopCategory.Weapon, 2_400, false), build.InvestmentIn(ShopCategory.Weapon));
        Assert.Equal(new BuildInvestment(ShopCategory.Vitality, 3_200, false), build.InvestmentIn(ShopCategory.Vitality));
        Assert.Equal(new BuildInvestment(ShopCategory.Spirit, 0, false), build.InvestmentIn(ShopCategory.Spirit));
        var shares = build.Shares();
        Assert.Equal(2_400 / 5_600.0, shares[ShopCategory.Weapon], 6);
        Assert.Equal(0, shares[ShopCategory.Spirit]);
        Assert.Equal(1.0, shares.Values.Sum(), 6);
    }

    [Fact]
    public void AnUnknownTierOrAHiddenCellMakesTheFigureAnEstimateTheBarCanRaise()
    {
        // Nothing but a hidden pick and a spirit item of unknown tier; the spirit bar is half full (about 6.9k souls).
        var build = HeroBuild.Merge(null, new HeroItems(0, [Hidden(0), Item(1, ShopCategory.Spirit, null, null)],
            new BarsRead(0.0, 0.5 * BarReader.TrackHeight, 0.0), false), _t0)!;

        var spirit = build.InvestmentIn(ShopCategory.Spirit);

        Assert.True(spirit.IsEstimate);
        Assert.InRange(spirit.Souls, 6_900, 6_950);
        Assert.True(build.InvestmentIn(ShopCategory.Weapon).IsEstimate);
        Assert.Equal(0, build.InvestmentIn(ShopCategory.Weapon).Souls);
    }

    [Fact]
    public void ABarNeverLowersWhatTheCellsSay()
    {
        var build = HeroBuild.Merge(null, new HeroItems(0, [Item(0, ShopCategory.Weapon, "a", 3), Hidden(1)], new BarsRead(0, 0, 0), false), _t0)!;

        Assert.Equal(3_200, build.InvestmentIn(ShopCategory.Weapon).Souls);
    }

    [Fact]
    public void ASavedBuildComesBackAsItWasAndDropsWhatNoLongerMakesSense()
    {
        var build = Build(Item(0, ShopCategory.Weapon, "active_reload", 1), Item(1, ShopCategory.Vitality, "gone_item", 2), Hidden(2), Unseen(3));

        var restored = HeroBuild.FromSaved(build.ToSaved(), id => id != "gone_item");

        Assert.Equal(["active_reload"], restored.ItemIds);
        Assert.Equal(_t0, restored.At);
        Assert.Equal(build.Bars, restored.Bars);
        Assert.Equal([CellKind.Item, CellKind.Item, CellKind.Hidden, CellKind.Unseen], restored.Cells.Select(cell => cell.Kind));
        Assert.Equal(2, restored.Cells[1].Tier);
        Assert.Equal(ShopCategory.Vitality, restored.Cells[1].Category);
    }

    [Fact]
    public void ASavedBuildWithBrokenCellsLoadsWhatItCan()
    {
        var saved = new SavedBuild
        {
            At = _t0,
            Cells =
            [
                new SavedBuildCell { Cell = 0, Kind = "item", Category = "weapon", ItemId = "a", Tier = 9 },
                new SavedBuildCell { Cell = 1, Kind = "item", Category = "nonsense" },
                new SavedBuildCell { Cell = 40, Kind = "item", Category = "spirit" },
            ],
        };

        var restored = HeroBuild.FromSaved(saved, _ => true);

        var cell = Assert.Single(restored.Cells);
        Assert.Equal("a", cell.ItemId);
        Assert.Null(cell.Tier);
        Assert.Null(restored.Bars);
    }
}
