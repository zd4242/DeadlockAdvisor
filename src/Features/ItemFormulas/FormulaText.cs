using Avalonia.Media;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match.Explain;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Formats;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Features.ItemFormulas;

/// <summary>The formula editors' wording and colours.</summary>
public static class FormulaText
{
    /// <summary>
    /// One per trait on the selected item, marking its rule card and its piece of each hero's sum in
    /// the preview. Clear of the relation colours.
    /// </summary>
    public static readonly IReadOnlyList<Color> TraitColors =
    [
        Color.Parse("#5fa8e8"), Color.Parse("#c08be8"), Color.Parse("#4cc6c0"), Color.Parse("#e88fc0"), Color.Parse("#a0b4c8"),
    ];

    public const string CoefficientTip =
        "Multiplier applied to the hero's 0-100 trait score.\n"
        + "2 = mild, 6 = strong, 10 = this item exists for this trait.\n"
        + "Negative to actively discourage the item.";

    public const string BestTargetTip =
        "Count only the best hero on the team for this trait in full, the next\n"
        + "×0.5, then ×0.25 and so on, instead of every hero who has it. For an\n"
        + "item one hero is enough to trigger: Reactive Barrier procs once per\n"
        + "cooldown, however many enemies can stun you. Covers the typed and\n"
        + "from-stats coefficients alike; never 'as', which is one hero.";

    public const string BestTargetFromCastTip =
        "Cast on one hero at a time (found by Data → Model Tools → Sync from Game API),\n"
        + "so every rule on this side already counts its best targets.";

    public const string BestTargetAsOnlyTip = "Only 'against' and 'with' can count best targets: 'as' is one hero.";

    public static string Who(Relation relation) => relation switch
    {
        Relation.Against => "an ENEMY",
        Relation.With => "an ALLY",
        _ => "YOUR OWN hero",
    };

    /// <summary>"Buy this when an ENEMY or YOUR OWN hero has this trait.", each who in its relation's colour.</summary>
    public static IReadOnlyList<TextSpan> BuyWhen(IEnumerable<Relation> relations)
    {
        var spans = new List<TextSpan> { new("Buy this when ") };
        var first = true;
        foreach (var relation in relations)
        {
            if (!first)
                spans.Add(new TextSpan(" or "));
            first = false;
            spans.Add(new TextSpan(Who(relation), Palette.RelationColor(relation)));
        }
        spans.Add(new TextSpan(" has this trait."));
        return spans;
    }

    /// <summary>An item row's rule-count badge: its typed rules, else whether its stats give it any.</summary>
    public static (string Text, Color Color) RulesBadge(int count, int derivedCount)
    {
        if (count > 0)
            return ($"{count} rule{(count != 1 ? "s" : "")}", Palette.Accent);
        if (derivedCount > 0)
            return ("stats only", Palette.TextDim);
        return ("untagged", Palette.TextFaint);
    }

    /// <summary>A preview number: whole from 10 up, like the Match page's scores, and below that a decimal only when there is one.</summary>
    public static string Amount(double value, bool signed = false)
    {
        var shown = Math.Abs(value) >= 10 ? Math.Round(value, MidpointRounding.ToEven) : NumberFormat.Round(value, 1);
        var text = Format.Num(shown);
        return signed && shown > 0 ? "+" + text : text;
    }

    /// <summary>"Slows: (80 − 27.1 avg) × 2.5 = +132.3".</summary>
    public static string Arithmetic(TraitPart part) =>
        $"{part.CategoryName}: {ExplainText.Arithmetic(part)} = {Format.SignedFixed(part.Share, 1)}";
}
