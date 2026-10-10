using Avalonia.Media;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match.Explain;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls;

/// <summary>
/// "data  enemies ▲1.3 · you ▼3.0": an item's match-data lifts, each relation named in its colour and
/// its lift signed by a green or red triangle.
/// </summary>
public class DataText : AmountLine
{
    public static readonly StyledProperty<OrderedDictionary<string, double>?> DataProperty =
        AvaloniaProperty.Register<DataText, OrderedDictionary<string, double>?>(nameof(Data));

    private static readonly IBrush _dataLabel = new SolidColorBrush(Palette.WithAlpha(Palette.Data, 170));

    private static readonly IBrush _against = new SolidColorBrush(Palette.RelationColor(Relation.Against));
    private static readonly IBrush _self = new SolidColorBrush(Palette.DataSelf);

    public OrderedDictionary<string, double>? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty)
            Rebuild();
    }

    protected override void Build()
    {
        Word("results", _dataLabel, gap: 8);
        foreach (var relation in new[] { Relation.Against, Relation.As })
        {
            if (Data is null || !Data.TryGetValue(relation.Key(), out var value))
                continue;
            Separate();
            Word(ExplainText.DataWord(relation.Key()), relation == Relation.Against ? _against : _self);
            Amount(value);
        }
    }
}
