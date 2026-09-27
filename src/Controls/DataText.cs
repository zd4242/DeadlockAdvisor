using Avalonia.Layout;
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
public class DataText : StackPanel
{
    public static readonly StyledProperty<OrderedDictionary<string, double>?> DataProperty =
        AvaloniaProperty.Register<DataText, OrderedDictionary<string, double>?>(nameof(Data));

    private static readonly IBrush _faint = new SolidColorBrush(Palette.TextFaint);
    private static readonly IBrush _dataLabel = new SolidColorBrush(Palette.WithAlpha(Palette.Data, 170));

    public DataText()
    {
        Orientation = Orientation.Horizontal;
        // Hit-testable between its pieces, so its tooltip shows anywhere along it.
        Background = Brushes.Transparent;
    }

    protected override Type StyleKeyOverride => typeof(StackPanel);

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
        Children.Clear();
        Children.Add(Label("data", _dataLabel, new Thickness(0, 0, 8, 0)));
        var first = true;
        foreach (var relation in new[] { Relation.Against, Relation.As })
        {
            if (Data is null || !Data.TryGetValue(relation.Key(), out var value))
                continue;
            if (!first)
                Children.Add(Label("·", _faint, new Thickness(5, 0)));
            first = false;
            Children.Add(Label(ExplainText.DataWord(relation.Key()), new SolidColorBrush(Palette.RelationColor(relation)), new Thickness(0, 0, 3, 0)));
            var lift = new DisplayAmount(value);
            Children.Add(new SignedAmount
            {
                Value = lift.Shown,
                Text = lift.Text,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }

    private static TextBlock Label(string text, IBrush brush, Thickness margin) =>
        new() { Text = text, Foreground = brush, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
}
