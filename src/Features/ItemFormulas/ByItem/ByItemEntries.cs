using Avalonia.Media;
using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Features.ItemFormulas.ByItem;

/// <summary>A rule the item gets from its stats via stat_rules.csv. Read-only here: change the stat rule (or re-sync the item) instead.</summary>
public sealed record DerivedRuleEntry(string CategoryName, Relation Relation, string Value, IReadOnlyList<string> Parts, Color TraitColor)
{
    public string RelationText => Relation.Key().ToUpperInvariant();
    public Color RelationColor => Theme.Palette.RelationColor(Relation);
}

/// <summary>One trait's share of a hero's sum in the preview, in the colour of its rule card.</summary>
public sealed record PreviewPiece(double Amount, Color Color, string Tip);

/// <param name="Pieces">One per trait column of the group; null where this hero's sum has no part from that trait.</param>
/// <param name="Tip">The arithmetic behind <see cref="Value"/>, trait by trait.</param>
public sealed record PreviewRow(string HeroId, string HeroName,IReadOnlyList<PreviewPiece?> Pieces, string Value, bool IsBest, string Tip);

/// <summary>The heroes an item's rules fire for on one relation, strongest first.</summary>
public sealed record PreviewGroup(string Title, Color Color, int TraitColumns, IReadOnlyList<PreviewRow> Rows);
