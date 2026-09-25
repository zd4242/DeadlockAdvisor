using Avalonia.Media;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Features.ItemFormulas.ByItem;

/// <summary>
/// One trait's share of a hero's sum in the preview: a small green ▲ or red ▼ for which way it pushes
/// the sum, then its size in the trait's colour. The triangle is drawn rather than typed so it can be
/// sized and centred on the digits; a glyph sits wherever the font puts it.
/// </summary>
public class PieceLabel : Control
{
    private const double _fontSize = 13;
    private const double _gap = 3;

    private readonly bool _up;
    private readonly FormattedText _text;
    private readonly double _capHeight;

    public PieceLabel(double amount, Color color)
    {
        _up = amount >= 0;
        _text = Fonts.Text(FormulaText.Amount(Math.Abs(amount)), _fontSize, color);
        _capHeight = _capHeightAtSize.Value;
    }

    /// <summary>How tall a capital stands at the label's size, measured off the glyph outline.</summary>
    private static readonly Lazy<double> _capHeightAtSize =
        new(() => Fonts.Text("H", _fontSize, Palette.Text).BuildGeometry(default)?.Bounds.Height ?? _fontSize * 0.7);

    private double Triangle => Math.Max(4.0, _capHeight * 0.62);

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Round(Triangle + _gap + _text.WidthIncludingTrailingWhitespace) + 1, _text.Height);

    public override void Render(DrawingContext context)
    {
        var textX = Bounds.Width - _text.WidthIncludingTrailingWhitespace;
        var top = (Bounds.Height - _text.Height) / 2;
        var middle = top + _text.Baseline - _capHeight / 2;

        // An equilateral triangle centred on the digits by its centroid, a third of the way up from
        // the base: centring its box instead leaves ▲ looking low and ▼ looking high.
        var size = Triangle;
        var left = textX - _gap - size;
        var height = size * 0.866;
        var direction = _up ? -1 : 1;
        var tip = middle + direction * height * 2 / 3;
        var baseY = middle - direction * height / 3;
        var triangle = new PolylineGeometry(
            [new Point(left, baseY), new Point(left + size, baseY), new Point(left + size / 2, tip)], isFilled: true);
        context.DrawGeometry(new SolidColorBrush(_up ? Palette.Positive : Palette.Negative), null, triangle);

        context.DrawText(_text, new Point(textX, top));
    }
}
