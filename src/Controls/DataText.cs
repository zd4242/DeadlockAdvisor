using Avalonia.Controls.Documents;
using Avalonia.Media;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match.Explain;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls;

/// <summary>"data  enemies +1.3 · you +3.0": an item's match-data lifts, each in its relation's colour.</summary>
public class DataText : TextBlock
{
    public static readonly StyledProperty<OrderedDictionary<string, double>?> DataProperty =
        AvaloniaProperty.Register<DataText, OrderedDictionary<string, double>?>(nameof(Data));

    private static readonly IBrush _faint = new SolidColorBrush(Palette.TextFaint);

    protected override Type StyleKeyOverride => typeof(TextBlock);

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

    private void Rebuild()
    {
        var inlines = new InlineCollection { new Run("data") { Foreground = _faint }, new Run("  ") };
        var first = true;
        foreach (var relation in new[] { Relation.Against, Relation.As })
        {
            if (Data is null || !Data.TryGetValue(relation.Key(), out var value))
                continue;
            if (!first)
                inlines.Add(new Run(" · "));
            first = false;
            inlines.Add(new Run($"{ExplainText.DataWord(relation.Key())} {Format.SignedFixed(value, 1)}")
            {
                Foreground = new SolidColorBrush(Palette.RelationColor(relation)),
            });
        }
        Inlines = inlines;
    }
}
