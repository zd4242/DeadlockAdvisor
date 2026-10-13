using Avalonia.Headless.XUnit;
using DeadlockAdvisor.Features.ItemFormulas.ByItem;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Tests.Ui;

/// <summary>The By Item page shows what an item pushes its holder's damage toward, and fixes it by hand, with the reason.</summary>
public class ItemLeanUiTests
{
    private const string Item = "bullet_resist_shredder";

    private static (UiHarness Ui, ItemLeanViewModel Lean, ByItemViewModel Page) Open()
    {
        var ui = new UiHarness(settings => UiHarness.Editing(settings, 3));
        ui.Data.Store.StatLeans["ShopSpiritPower"] = new StatLean("ShopSpiritPower", -0.25);
        ui.Data.Store.StatLeans["BulletResistReduction"] = new StatLean("BulletResistReduction", 0.75);
        ui.Data.Store.ItemStats[Item] = [new ItemStat(Item, "ShopSpiritPower", "Spirit Power (shop bonus)", 22, "", false)];
        ui.Data.Store.ItemEffects[Item] = [new ItemEffect(Item, "BulletResistReduction", EffectScope.EnemyDebuff, 10)];
        ui.Data.Store.RebuildDerived();
        var page = ui.ViewModel.ItemFormulas.ByItem;
        page.SelectedRow = page.Items.Single(row => row.ItemId == Item);
        return (ui, page.Lean, page);
    }

    [AvaloniaFact]
    public void ASpiritItemThatLowersBulletResistShowsItsStatsAndLeansTowardBullet()
    {
        var (ui, lean, _) = Open();
        using var _ui = ui;
        ui.Show();

        Assert.Equal("+0.50 toward bullet damage", lean.Summary);
        Assert.Equal("from its stats", lean.Source);
        Assert.Equal(["Spirit Power (shop bonus) -0.25", "BulletResistReduction +0.75"], lean.Votes);
        Assert.True(lean.IsAuto);
        Assert.True(File.Exists(ui.Screenshot("by_item_lean.png")));
    }

    [AvaloniaFact]
    public void FixingItNeedsAReasonAndIsSavedWithIt()
    {
        var (ui, lean, page) = Open();
        using var _ui = ui;
        var store = ui.Data.Store;

        lean.ChooseCommand.Execute(LeanChoice.Spirit).Subscribe();
        Assert.NotNull(lean.Hint);
        Assert.Empty(store.ItemLeanOverrides);

        lean.Reason = "Tested: its buyers deal spirit damage";
        lean.ChooseCommand.Execute(LeanChoice.Spirit).Subscribe();

        var entry = Assert.Single(store.ItemLeanOverrides).Value;
        Assert.Equal((Item, -1.0, "Tested: its buyers deal spirit damage"), (entry.ItemId, entry.Lean, entry.Reason));
        Assert.True(lean.IsSpirit);
        Assert.Equal("-1.00 toward spirit damage", lean.Summary);
        Assert.Equal("set by hand; its stats say +0.50", lean.Source);
        Assert.True(page.Items.Single(row => row.ItemId == Item).HasLeanOverride);

        // Mending the reason keeps the choice.
        lean.Reason = "Tested: spirit damage again";
        Assert.Equal("Tested: spirit damage again", store.ItemLeanOverrides[Item].Reason);
        Assert.Equal(-1.0, store.ItemLeanOverrides[Item].Lean);

        lean.ChooseCommand.Execute(LeanChoice.Auto).Subscribe();
        Assert.Empty(store.ItemLeanOverrides);
        Assert.True(lean.IsAuto);
        Assert.False(page.Items.Single(row => row.ItemId == Item).HasLeanOverride);
        Assert.Equal("+0.50 toward bullet damage", lean.Summary);
    }

    [AvaloniaFact]
    public void AnEditIsWrittenToTheDataFolder()
    {
        var (ui, lean, _) = Open();
        using var _ui = ui;

        lean.Reason = "Its debuff is the point";
        lean.ChooseCommand.Execute(LeanChoice.Bullet).Subscribe();
        ui.Data.FlushSaves();

        var text = File.ReadAllText(Path.Combine(ui.Data.DataDir, DataStore.ItemLeanOverridesFile));
        Assert.Contains($"{Item},1,Its debuff is the point", text, StringComparison.Ordinal);
    }
}
