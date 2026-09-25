using Avalonia.Media;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Theme;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.ItemFormulas.ByItem;

/// <summary>One item in the By Item list, kept for the life of the loaded data; only its rule badge changes.</summary>
public class ItemRowViewModel : ViewModelBase
{
    public ItemRowViewModel(Item item)
    {
        Item = item;
        ShopColor = Palette.ShopColor(item.Category);
        TierColor = Palette.TierColor(item.Tier);
    }

    public Item Item { get; }
    public string ItemId => Item.ItemId;
    public string Name => Item.ItemName;
    public int Tier => Item.Tier;
    public string TierText => $"T{Item.Tier}";
    public Color TierColor { get; }
    public string ShopText => (Item.Category.Length > 3 ? Item.Category[..3] : Item.Category).ToUpperInvariant();
    public Color ShopColor { get; }

    [Reactive] public string RulesText { get; private set; } = "";
    [Reactive] public Color RulesColor { get; private set; } = Palette.TextFaint;

    public void RefreshRules(DataStore store) =>
        (RulesText, RulesColor) = FormulaText.RulesBadge(store.RuleCount(ItemId), store.DerivedRuleCount(ItemId));
}
