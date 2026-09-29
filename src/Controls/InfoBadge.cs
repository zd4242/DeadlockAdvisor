using Avalonia.Input;
using Avalonia.Media;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls;

/// <summary>A circled "i" that explains something in its tooltip; set the text with <c>ToolTip.Tip</c>.</summary>
public class InfoBadge : Control
{
    private const double _size = 15;
    private const double _fontSize = 11;
    private const int _tipDelay = 100;

    static InfoBadge()
    {
        AffectsRender<InfoBadge>(IsPointerOverProperty);
        CursorProperty.OverrideDefaultValue<InfoBadge>(new Cursor(StandardCursorType.Help));
        // Hovering the badge is asking for its tip, so it comes up almost at once.
        ToolTip.ShowDelayProperty.OverrideDefaultValue<InfoBadge>(_tipDelay);
    }

    protected override Size MeasureOverride(Size availableSize) => new(_size, _size);

    public override void Render(DrawingContext context)
    {
        var color = IsPointerOver ? Palette.Text : Palette.TextDim;
        var circle = new Rect(0.5, 0.5, _size - 1, _size - 1);
        context.DrawEllipse(new SolidColorBrush(Palette.WithAlpha(color, 30)), new Pen(new SolidColorBrush(color)), circle);
        var glyph = Fonts.Text("i", _fontSize, color, bold: true);
        context.DrawText(glyph, new Point((_size - glyph.Width) / 2, (_size - glyph.Height) / 2));
    }
}
