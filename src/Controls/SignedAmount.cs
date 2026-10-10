using Avalonia.Automation.Peers;
using Avalonia.Media;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls;

/// <summary>
/// A signed number as a small green ▲ or red ▼ for its direction, then its size. The triangle is
/// drawn rather than typed so it can be sized and centred on the digits; a glyph sits wherever the
/// font puts it. Zero draws no triangle but keeps its room, so a column of these stays aligned.
/// </summary>
public class SignedAmount : Control
{
    private const double _gap = 3;

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SignedAmount, double>(nameof(Value));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<SignedAmount, string?>(nameof(Text));

    public static readonly StyledProperty<Color?> ColorProperty =
        AvaloniaProperty.Register<SignedAmount, Color?>(nameof(Color));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<SignedAmount, double>(nameof(FontSize), 13);

    public static readonly StyledProperty<bool> IsBoldProperty =
        AvaloniaProperty.Register<SignedAmount, bool>(nameof(IsBold));

    private static readonly Dictionary<double, double> _capHeights = [];

    private FormattedText? _text;

    static SignedAmount()
    {
        AffectsMeasure<SignedAmount>(ValueProperty, TextProperty, FontSizeProperty, IsBoldProperty);
        AffectsRender<SignedAmount>(ValueProperty, TextProperty, ColorProperty, FontSizeProperty, IsBoldProperty);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>The size as printed, without a sign; the value's magnitude when unset.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The digits' colour; unset, they take the triangle's.</summary>
    public Color? Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public bool IsBold
    {
        get => GetValue(IsBoldProperty);
        set => SetValue(IsBoldProperty, value);
    }

    private Color SignColor => Value > 0 ? Palette.Positive : Value < 0 ? Palette.Negative : Palette.TextDim;

    private FormattedText Formatted =>
        _text ??= Fonts.Text(Text ?? Format.Num(Math.Abs(Value)), FontSize, Color ?? SignColor, IsBold);

    /// <summary>How tall a capital stands at this size, measured off the glyph outline.</summary>
    private double CapHeight
    {
        get
        {
            if (!_capHeights.TryGetValue(FontSize, out var height))
            {
                height = Fonts.Text("H", FontSize, Palette.Text).BuildGeometry(default)?.Bounds.Height ?? FontSize * 0.7;
                _capHeights[FontSize] = height;
            }
            return height;
        }
    }

    private double Triangle => Math.Max(4.0, CapHeight * 0.62);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == TextProperty || change.Property == ColorProperty
            || change.Property == FontSizeProperty || change.Property == IsBoldProperty)
            _text = null;
    }

    // The triangle says which way it moved; a reader needs it said.
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new PaintedPeer(this, AutomationControlType.Text, () =>
        {
            var size = Text ?? Format.Num(Math.Abs(Value));
            return Value > 0 ? $"up {size}" : Value < 0 ? $"down {size}" : size;
        });

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Round(Triangle + _gap + Formatted.WidthIncludingTrailingWhitespace) + 1, Formatted.Height);

    public override void Render(DrawingContext context)
    {
        var text = Formatted;
        var textX = Bounds.Width - text.WidthIncludingTrailingWhitespace;
        var top = (Bounds.Height - text.Height) / 2;
        context.DrawText(text, new Point(textX, top));
        if (Value == 0)
            return;

        // An equilateral triangle centred on the digits by its centroid, a third of the way up from
        // the base: centring its box instead leaves ▲ looking low and ▼ looking high.
        var middle = top + text.Baseline - CapHeight / 2;
        var size = Triangle;
        var left = textX - _gap - size;
        var height = size * 0.866;
        var direction = Value > 0 ? -1 : 1;
        var tip = middle + direction * height * 2 / 3;
        var baseY = middle - direction * height / 3;
        var triangle = new PolylineGeometry(
            [new Point(left, baseY), new Point(left + size, baseY), new Point(left + size / 2, tip)], isFilled: true);
        context.DrawGeometry(new SolidColorBrush(SignColor), null, triangle);
    }
}
