using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using DeadlockAdvisor.Controls.Art;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls;

/// <param name="IsSelf">You, drawn in gold and always counted in your own lane.</param>
public sealed record SlotEntry(string HeroId, string Name, bool IsSelf, bool InLane);

/// <summary>
/// One team on the roster, drawn as six fixed slots in one control. You come first, then laners,
/// then everyone else by name, so the lane matchup is one run at the left with a single LANE
/// bracket under it. Empty slots are dashed outlines, so the strip is the same height with no
/// heroes or six.
/// <para>
/// A portrait click toggles lane, its × removes, right click opens the role menu, and an empty
/// slot starts filling this team.
/// </para>
/// </summary>
public class SlotStrip : Control
{
    public const int Slots = 6;
    private const double _maxPortrait = 80;
    private const double _minPortrait = 36;
    private const double _gap = 8;
    private const double _nameHeight = 16;
    private const double _radius = 8;

    public static readonly StyledProperty<IReadOnlyList<SlotEntry>> EntriesProperty =
        AvaloniaProperty.Register<SlotStrip, IReadOnlyList<SlotEntry>>(nameof(Entries), []);

    public static readonly StyledProperty<Color> TeamColorProperty =
        AvaloniaProperty.Register<SlotStrip, Color>(nameof(TeamColor), Palette.Ally);

    /// <summary>"ally" or "enemy", for the empty slots' tooltip.</summary>
    public static readonly StyledProperty<string> NounProperty =
        AvaloniaProperty.Register<SlotStrip, string>(nameof(Noun), "ally");

    public static readonly RoutedEvent<HeroEventArgs> RemovedEvent =
        RoutedEvent.Register<SlotStrip, HeroEventArgs>("Removed", RoutingStrategies.Bubble);

    public static readonly RoutedEvent<HeroEventArgs> LaneToggledEvent =
        RoutedEvent.Register<SlotStrip, HeroEventArgs>("LaneToggled", RoutingStrategies.Bubble);

    public static readonly RoutedEvent<HeroEventArgs> RightClickedEvent =
        RoutedEvent.Register<SlotStrip, HeroEventArgs>("RightClicked", RoutingStrategies.Bubble);

    public static readonly RoutedEvent<RoutedEventArgs> EmptyClickedEvent =
        RoutedEvent.Register<SlotStrip, RoutedEventArgs>("EmptyClicked", RoutingStrategies.Bubble);

    private IReadOnlyList<SlotEntry> _sorted = [];
    private double _portrait = _maxPortrait;
    private int _hover = -1;
    private bool _hoverRemove;

    static SlotStrip()
    {
        AffectsRender<SlotStrip>(TeamColorProperty, ArtHost.ServiceProperty, ArtHost.RevisionProperty);
    }

    public IReadOnlyList<SlotEntry> Entries
    {
        get => GetValue(EntriesProperty);
        set => SetValue(EntriesProperty, value);
    }

    public Color TeamColor
    {
        get => GetValue(TeamColorProperty);
        set => SetValue(TeamColorProperty, value);
    }

    public string Noun
    {
        get => GetValue(NounProperty);
        set => SetValue(NounProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EntriesProperty)
        {
            // Hover survives: slots don't move, so the cursor is still over the same one after a
            // lane toggle or a removal.
            _sorted = Entries
                .OrderBy(entry => !entry.IsSelf)
                .ThenBy(entry => !entry.InLane)
                .ThenBy(entry => entry.Name.ToLowerInvariant(), StringComparer.Ordinal)
                .Take(Slots)
                .ToList();
            UpdateToolTip();
            InvalidateVisual();
        }
    }

    // -- sizing ---------------------------------------------------------------

    /// <summary>Fill the row, capped so a wide window doesn't get poster-sized slots, and snapped to even sizes.</summary>
    private static double PortraitFor(double width)
    {
        if (double.IsInfinity(width))
            return _maxPortrait;
        var fit = Math.Floor((width - 2 - _gap * (Slots - 1)) / Slots);
        fit -= fit % 2;
        return Math.Max(_minPortrait, Math.Min(_maxPortrait, fit));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var portrait = PortraitFor(availableSize.Width);
        var minWidth = _minPortrait * Slots + _gap * (Slots - 1) + 2;
        var width = double.IsInfinity(availableSize.Width) ? portrait * Slots + _gap * (Slots - 1) + 2 : Math.Max(minWidth, availableSize.Width);
        // portrait, name, then room for the lane bracket
        return new Size(width, portrait + 34);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _portrait = PortraitFor(finalSize.Width);
        return finalSize;
    }

    // -- geometry -------------------------------------------------------------

    private Rect SlotRect(int index) => new(1 + index * (_portrait + _gap), 1, _portrait, _portrait);

    private Rect RemoveRect(int index)
    {
        var slot = SlotRect(index);
        const double diameter = 18;
        const double inset = 3;
        return new Rect(slot.Right - diameter - inset, slot.Top + inset, diameter, diameter);
    }

    private (int Index, bool OnRemove) Hit(Point point)
    {
        for (var index = 0; index < Slots; index++)
        {
            if (!SlotRect(index).Contains(point))
                continue;
            var onRemove = index < _sorted.Count && RemoveRect(index).Contains(point);
            return (index, onRemove);
        }
        return (-1, false);
    }

    // -- input ----------------------------------------------------------------

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var (index, onRemove) = Hit(e.GetPosition(this));
        if ((index, onRemove) == (_hover, _hoverRemove))
            return;
        _hover = index;
        _hoverRemove = onRemove;
        Cursor = index >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        UpdateToolTip();
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        _hoverRemove = false;
        UpdateToolTip();
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var (index, onRemove) = Hit(e.GetPosition(this));
        if (index < 0)
            return;
        var properties = e.GetCurrentPoint(this).Properties;
        e.Handled = true;

        if (index >= _sorted.Count)
        {
            if (properties.IsLeftButtonPressed)
                RaiseEvent(new RoutedEventArgs(EmptyClickedEvent));
            return;
        }

        var entry = _sorted[index];
        if (properties.IsRightButtonPressed)
            RaiseEvent(new HeroEventArgs(RightClickedEvent, entry.HeroId));
        else if (properties.IsLeftButtonPressed && onRemove)
            RaiseEvent(new HeroEventArgs(RemovedEvent, entry.HeroId));
        // You're always in your own lane.
        else if (properties.IsLeftButtonPressed && !entry.IsSelf)
            RaiseEvent(new HeroEventArgs(LaneToggledEvent, entry.HeroId));
    }

    private void UpdateToolTip()
    {
        string? tip = null;
        if (_hover >= 0 && _hover >= _sorted.Count)
        {
            tip = $"Empty slot -- click to add an {Noun}";
        }
        else if (_hover >= 0)
        {
            var entry = _sorted[_hover];
            if (_hoverRemove)
                tip = $"Remove {entry.Name} from the match";
            else if (entry.IsSelf)
                tip = $"{entry.Name} (you)";
            else
                tip = $"{entry.Name} -- click to {(entry.InLane ? "take out of" : "put in")} your lane\nRight click for more";
        }
        ToolTip.SetTip(this, tip);
    }

    // -- painting ---------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        for (var index = 0; index < Slots; index++)
        {
            if (index < _sorted.Count)
                PaintHero(context, index);
            else
                PaintEmpty(context, index);
        }
        PaintLaneBracket(context);
    }

    private void PaintEmpty(DrawingContext context, int index)
    {
        var rect = SlotRect(index);
        var hovered = index == _hover;
        var pen = new Pen(new SolidColorBrush(hovered ? TeamColor : Palette.BorderStrong), 1, new DashStyle([4, 2], 0));
        context.DrawRectangle(new SolidColorBrush(hovered ? Palette.Surface3 : Palette.Surface2), pen, new RoundedRect(rect, _radius));
        var plus = Fonts.Text("+", 20, hovered ? TeamColor : Palette.TextFaint);
        context.DrawText(plus, new Point(rect.X + (rect.Width - plus.Width) / 2, rect.Y + (rect.Height - plus.Height) / 2));
    }

    private void PaintHero(DrawingContext context, int index)
    {
        var entry = _sorted[index];
        var rect = SlotRect(index);
        var hovered = index == _hover;

        ArtPainter.Draw(context, this, ArtKind.Hero, entry.HeroId, entry.Name, rect, _radius);
        var ring = new Pen(new SolidColorBrush(entry.IsSelf ? Palette.Accent : TeamColor), hovered ? 3 : 2);
        context.DrawRectangle(null, ring, new RoundedRect(rect, _radius));

        if (hovered)
        {
            var badge = RemoveRect(index);
            var back = Palette.WithAlpha(Palette.Bg, (byte)(_hoverRemove ? 255 : 200));
            context.DrawEllipse(new SolidColorBrush(back), null, badge);
            var cross = Fonts.Text("×", 13, _hoverRemove ? Palette.Enemy : Palette.Text, bold: true);
            context.DrawText(cross, new Point(badge.X + (badge.Width - cross.Width) / 2, badge.Y + (badge.Height - cross.Height) / 2));
        }

        var nameWidth = rect.Width + _gap;
        var color = entry.IsSelf || entry.InLane || hovered ? Palette.Text : Palette.TextDim;
        var name = Fonts.Centered(entry.Name, 11, color, nameWidth, bold: entry.IsSelf);
        context.DrawText(name, new Point(rect.Left - _gap / 2, rect.Bottom + 3 + (_nameHeight - name.Height) / 2));
    }

    /// <summary>One bracket under you plus the laners. Skipped until someone is marked for lane: you alone isn't a matchup yet.</summary>
    private void PaintLaneBracket(DrawingContext context)
    {
        if (!_sorted.Any(entry => entry.InLane))
            return;
        var run = _sorted.Select((entry, index) => (entry, index))
            .Where(pair => pair.entry.IsSelf || pair.entry.InLane)
            .Select(pair => pair.index)
            .ToList();

        const double inset = 4;
        const double tick = 4;
        var x0 = SlotRect(run[0]).Left + inset;
        var x1 = SlotRect(run[^1]).Right - inset;
        var y = _portrait + 27;

        var pen = new Pen(new SolidColorBrush(Palette.WithAlpha(Palette.Accent, 170)), 2, lineCap: PenLineCap.Round);
        context.DrawLine(pen, new Point(x0, y), new Point(x1, y));
        context.DrawLine(pen, new Point(x0, y), new Point(x0, y - tick));
        context.DrawLine(pen, new Point(x1, y), new Point(x1, y - tick));

        var glyphs = "LANE".Select(c => Fonts.Text(c.ToString(), 9, Palette.Accent, bold: true)).ToList();
        const double spacing = 1;
        var textWidth = glyphs.Sum(glyph => glyph.WidthIncludingTrailingWhitespace + spacing);
        var tagWidth = textWidth + 10;
        var tag = new Rect((x0 + x1 - tagWidth) / 2, y - 6, tagWidth, 12);
        // The card behind us, so the label sits in a gap in the line.
        context.DrawRectangle(new SolidColorBrush(Palette.Surface), null, tag);
        DrawSpaced(context, glyphs, tag, spacing);
    }

    /// <summary>Glyphs laid out with extra letter spacing, centred in <paramref name="rect"/>.</summary>
    private static void DrawSpaced(DrawingContext context, IReadOnlyList<FormattedText> glyphs, Rect rect, double spacing)
    {
        var width = glyphs.Sum(glyph => glyph.WidthIncludingTrailingWhitespace) + spacing * (glyphs.Count - 1);
        var x = rect.X + (rect.Width - width) / 2;
        foreach (var glyph in glyphs)
        {
            context.DrawText(glyph, new Point(x, rect.Y + (rect.Height - glyph.Height) / 2));
            x += glyph.WidthIncludingTrailingWhitespace + spacing;
        }
    }
}
