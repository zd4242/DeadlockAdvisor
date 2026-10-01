using Avalonia.Media;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Formats;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Features.ItemFormulas.ByTrait;

/// <summary>
/// One item in the By Trait grid, kept for the life of the loaded data. Its numbers are for the
/// selected trait + relation, refreshed in place when that or the data changes.
/// </summary>
public sealed class CoefficientRow(Item item)
{
    public Item Item { get; } = item;
    public string ItemId => Item.ItemId;
    public string Name => Item.ItemName;
    public string TierText => $"T{Item.Tier}";
    public Color TierColor { get; } = Palette.TierColor(item.Tier);
    public string Shop => Item.Category;
    public Color ShopColor { get; } = Palette.ShopColor(item.Category);

    /// <summary>The hand-typed coefficient.</summary>
    public double Coefficient { get; private set; }

    /// <summary>What the item's stats add through stat_rules.csv.</summary>
    public double FromStats { get; private set; }

    public string FromStatsText => FromStats == 0 ? "" : Format.Num(NumberFormat.Round(FromStats, 2));

    /// <summary>The arithmetic behind <see cref="FromStats"/>, a line per stat; null when there's none.</summary>
    public string? FromStatsTip { get; private set; }

    /// <summary>Has anything on this trait: a typed number or a stat-derived one.</summary>
    public bool IsTagged => Coefficient != 0 || FromStats != 0;

    /// <summary>The relation can count best targets at all: not "as", which is only ever one hero.</summary>
    public bool BestTargetApplies { get; private set; }

    /// <summary>This line counts its best targets (<see cref="DataStore.OnBestTargets"/>), marked or because the item is cast on one hero.</summary>
    public bool BestTarget { get; private set; }

    /// <summary>The item is cast on one hero of the team, so every line on the relation counts its best targets, marked or not.</summary>
    public bool BestTargetFromCast { get; private set; }

    /// <summary>Its place in the sorted order, hidden rows included; the stripes follow it.</summary>
    public int OrderIndex { get; set; }

    public void Refresh(DataStore store, string? categoryId, Relation relation)
    {
        if (string.IsNullOrEmpty(categoryId))
        {
            (Coefficient, FromStats, FromStatsTip) = (0, 0, null);
            (BestTargetApplies, BestTarget, BestTargetFromCast) = (false, false, false);
            return;
        }
        Coefficient = store.Coefficient(ItemId, categoryId, relation);
        FromStats = store.DerivedCoefficient(ItemId, categoryId, relation);
        var parts = store.DerivedParts(ItemId, categoryId, relation);
        FromStatsTip = parts.Count == 0 ? null : string.Join("\n", parts.Select(part => part.Describe()));
        BestTargetApplies = relation != Relation.As;
        BestTarget = BestTargetApplies && store.OnBestTargets(ItemId, categoryId, relation);
        BestTargetFromCast = BestTargetApplies && store.CastOnCovers(ItemId, relation);
    }
}
