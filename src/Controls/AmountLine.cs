using Avalonia.Layout;
using Avalonia.Media;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls;

/// <summary>
/// A line of coloured words and signed amounts, "enemies ▲1.3 · you ▼3.0", rebuilt whenever what it
/// shows changes.
/// </summary>
public abstract class AmountLine : StackPanel
{
    protected static readonly IBrush Faint = new SolidColorBrush(Palette.TextFaint);

    private bool _separate;

    protected AmountLine()
    {
        Orientation = Orientation.Horizontal;
        // Hit-testable between its pieces, so its tooltip shows anywhere along it.
        Background = Brushes.Transparent;
    }

    protected override Type StyleKeyOverride => typeof(StackPanel);

    /// <summary>Fill the line afresh from <see cref="Word"/>, <see cref="Amount"/> and <see cref="Separate"/>.</summary>
    protected abstract void Build();

    protected void Rebuild()
    {
        Children.Clear();
        _separate = false;
        Build();
    }

    protected void Word(string text, IBrush brush, double gap = 3) =>
        Children.Add(new TextBlock
        {
            Text = text,
            Foreground = brush,
            Margin = new Thickness(0, 0, gap, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });

    protected void Amount(double value, bool bold = false)
    {
        var amount = new DisplayAmount(value);
        Children.Add(new SignedAmount
        {
            Value = amount.Shown,
            Text = amount.Text,
            IsBold = bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    /// <summary>A "·" before every part but the first.</summary>
    protected void Separate(string separator = "·")
    {
        if (_separate)
            Children.Add(new TextBlock
            {
                Text = separator,
                Foreground = Faint,
                Margin = new Thickness(5, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        _separate = true;
    }
}
