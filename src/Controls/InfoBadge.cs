using Avalonia.Automation.Peers;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls;

/// <summary>
/// A circled "i" that explains something in its tooltip; set the text with <c>ToolTip.Tip</c>.
/// Clicking it keeps the text open, which a tooltip can't, until the next click anywhere.
/// </summary>
public class InfoBadge : Control
{
    private const double _size = 15;
    private const double _fontSize = 11;
    private const int _tipDelay = 100;
    private const double _pinnedWidth = 320;

    /// <summary>A press that dismisses the pinned text lands on the badge too; it must not bring it straight back.</summary>
    private static readonly TimeSpan _reopenGuard = TimeSpan.FromMilliseconds(250);

    private readonly TextBlock _pinnedText = new()
    {
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = _pinnedWidth,
        Foreground = new SolidColorBrush(Palette.Text),
    };

    private Flyout? _pinned;
    private long _closedAt;

    static InfoBadge()
    {
        AffectsRender<InfoBadge>(IsPointerOverProperty);
        CursorProperty.OverrideDefaultValue<InfoBadge>(new Cursor(StandardCursorType.Help));
        // Hovering the badge is asking for its tip, so it comes up almost at once.
        ToolTip.ShowDelayProperty.OverrideDefaultValue<InfoBadge>(_tipDelay);
    }

    /// <summary>Whether a click is holding the text open.</summary>
    public bool IsPinned => _pinned?.IsOpen == true;

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new PaintedPeer(this, AutomationControlType.Button, () => "More information", () => ToolTip.GetTip(this)?.ToString());

    protected override Size MeasureOverride(Size availableSize) => new(_size, _size);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || ToolTip.GetTip(this)?.ToString() is not { Length: > 0 } text)
            return;

        e.Handled = true;
        var flyout = _pinned ??= CreateFlyout();
        if (flyout.IsOpen)
            flyout.Hide();
        else if (Environment.TickCount64 - _closedAt > _reopenGuard.TotalMilliseconds)
        {
            _pinnedText.Text = text;
            flyout.ShowAt(this);
        }
    }

    private Flyout CreateFlyout()
    {
        var flyout = new Flyout { Placement = PlacementMode.Bottom, Content = _pinnedText };
        flyout.Closed += (_, _) => _closedAt = Environment.TickCount64;
        return flyout;
    }

    public override void Render(DrawingContext context)
    {
        var color = IsPointerOver ? Palette.Text : Palette.TextDim;
        var circle = new Rect(0.5, 0.5, _size - 1, _size - 1);
        context.DrawEllipse(new SolidColorBrush(Palette.WithAlpha(color, 30)), new Pen(new SolidColorBrush(color)), circle);
        var glyph = Fonts.Text("i", _fontSize, color, bold: true);
        context.DrawText(glyph, new Point((_size - glyph.Width) / 2, (_size - glyph.Height) / 2));
    }
}
