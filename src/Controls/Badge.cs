using Avalonia.Automation.Peers;
using Avalonia.Media;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls;

/// <summary>A small coloured pill: tier numbers, shop categories, relations, rule counts. Painted rather than templated, since there are hundreds.</summary>
public class Badge : Control
{
    public const double FontSize = 11;
    private const double _padX = 6;
    private const double _padY = 1;
    private const double _radius = 4;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<Badge, string?>(nameof(Text));

    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<Badge, Color>(nameof(Color), Palette.TextFaint);

    static Badge()
    {
        AffectsRender<Badge>(TextProperty, ColorProperty);
        AffectsMeasure<Badge>(TextProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new PaintedPeer(this, AutomationControlType.Text, () => Text);

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Fonts.Text(Text ?? "", FontSize, Color, bold: true);
        return new Size(text.Width + _padX * 2, text.Height + _padY * 2);
    }

    public override void Render(DrawingContext context)
    {
        Paint(context, new Rect(Bounds.Size), Text ?? "", Color);
    }

    /// <summary>The pill itself, for views that paint their rows instead of filling them with controls.</summary>
    public static void Paint(DrawingContext context, Rect rect, string text, Color color)
    {
        context.DrawRectangle(new SolidColorBrush(Palette.WithAlpha(color, 38)), null, new RoundedRect(rect, _radius));
        var formatted = Fonts.Text(text, FontSize, color, bold: true);
        context.DrawText(formatted, new Point(rect.X + (rect.Width - formatted.Width) / 2, rect.Y + (rect.Height - formatted.Height) / 2));
    }

    public static Size Measure(string text)
    {
        var formatted = Fonts.Text(text, FontSize, Palette.Text, bold: true);
        return new Size(formatted.Width + _padX * 2, formatted.Height + _padY * 2);
    }
}
