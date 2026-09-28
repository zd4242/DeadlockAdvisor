using Avalonia.Media;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match.Explain;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls;

/// <summary>
/// "data  enemies ▲1.3 · you ▼3.0": an item's match-data lifts, each relation named in its colour and
/// its lift signed by a green or red triangle, then "rarely built" when your hero rarely builds it.
/// </summary>
public class DataText : AmountLine
{
    public static readonly StyledProperty<OrderedDictionary<string, double>?> DataProperty =
        AvaloniaProperty.Register<DataText, OrderedDictionary<string, double>?>(nameof(Data));

    public static readonly StyledProperty<bool> IsRarelyBuiltProperty =
        AvaloniaProperty.Register<DataText, bool>(nameof(IsRarelyBuilt));

    private static readonly IBrush _dataLabel = new SolidColorBrush(Palette.WithAlpha(Palette.Data, 170));

    public OrderedDictionary<string, double>? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public bool IsRarelyBuilt
    {
        get => GetValue(IsRarelyBuiltProperty);
        set => SetValue(IsRarelyBuiltProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty || change.Property == IsRarelyBuiltProperty)
            Rebuild();
    }

    protected override void Build()
    {
        Word("data", _dataLabel, gap: 8);
        foreach (var relation in new[] { Relation.Against, Relation.As })
        {
            if (Data is null || !Data.TryGetValue(relation.Key(), out var value))
                continue;
            Separate();
            RelationWord(relation);
            Amount(value);
        }

        if (!IsRarelyBuilt)
            return;
        Separate();
        // Without a lift of its own, "you" still says whose building it is.
        if (Data is null || !Data.ContainsKey(Relation.As.Key()))
            RelationWord(Relation.As);
        Word("rarely built", Faint, gap: 0);
    }

    private void RelationWord(Relation relation) =>
        Word(ExplainText.DataWord(relation.Key()), new SolidColorBrush(Palette.RelationColor(relation)));
}
