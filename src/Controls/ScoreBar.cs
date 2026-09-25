using Avalonia.Media;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls;

/// <summary>A horizontal magnitude bar behind a recommendation's score, relative to the best score on screen.</summary>
public class ScoreBar : Control
{
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<ScoreBar, double>(nameof(Fraction));

    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<ScoreBar, Color>(nameof(Color), Palette.Accent);

    private static readonly IBrush _track = new SolidColorBrush(Palette.WithAlpha(Palette.Border, 120));

    static ScoreBar()
    {
        AffectsRender<ScoreBar>(FractionProperty, ColorProperty);
        HeightProperty.OverrideDefaultValue<ScoreBar>(6);
    }

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var height = Bounds.Height;
        var radius = height / 2;
        context.DrawRectangle(_track, null, new RoundedRect(new Rect(Bounds.Size), radius));

        var fraction = Math.Clamp(Fraction, 0, 1);
        if (fraction <= 0)
            return;
        var width = Math.Max(height, Bounds.Width * fraction);
        context.DrawRectangle(new SolidColorBrush(Color), null, new RoundedRect(new Rect(0, 0, width, height), radius));
    }
}
