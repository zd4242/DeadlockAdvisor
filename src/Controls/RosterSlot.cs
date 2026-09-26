using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using DeadlockAdvisor.Controls.Art;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls;

/// <summary>
/// One place on a team's side of the match bar: a hero's portrait and name in a ring of the team's
/// colour (gold for you), or a dashed outline while it's empty. The portrait grows with the width
/// it's given, up to a cap, so a row of slots fills the bar on a narrow window without turning
/// poster-sized on a wide one.
/// <para>
/// Once the match has net worth, a pill under the portrait shows it the way the game's top bar does.
/// A portrait click toggles lane, its × removes, right click opens the role menu, and an empty
/// slot starts filling this team.
/// </para>
/// </summary>
public class RosterSlot : Control
{
    private const double _maxPortrait = 80;
    private const double _minPortrait = 36;
    private const double _gap = 8;
    // Room above the portrait for the ring's stroke.
    private const double _top = 1;
    private const double _nameHeight = 16;
    private const double _pillHeight = 16;
    private const double _radius = 8;

    public static readonly StyledProperty<string?> HeroIdProperty =
        AvaloniaProperty.Register<RosterSlot, string?>(nameof(HeroId));

    public static readonly StyledProperty<string> HeroNameProperty =
        AvaloniaProperty.Register<RosterSlot, string>(nameof(HeroName), "");

    /// <summary>Ally or Enemy: the ring colour, and who an empty slot adds.</summary>
    public static readonly StyledProperty<Role> TeamProperty =
        AvaloniaProperty.Register<RosterSlot, Role>(nameof(Team), Role.Ally);

    public static readonly StyledProperty<bool> IsSelfProperty =
        AvaloniaProperty.Register<RosterSlot, bool>(nameof(IsSelf));

    public static readonly StyledProperty<bool> InLaneProperty =
        AvaloniaProperty.Register<RosterSlot, bool>(nameof(InLane));

    /// <summary>The hero's net worth in souls, when it's been read.</summary>
    public static readonly StyledProperty<int?> NetWorthProperty =
        AvaloniaProperty.Register<RosterSlot, int?>(nameof(NetWorth));

    /// <summary>How the net worth moved since the reading before, for the tooltip.</summary>
    public static readonly StyledProperty<string?> NetWorthChangeProperty =
        AvaloniaProperty.Register<RosterSlot, string?>(nameof(NetWorthChange));

    /// <summary>Room for a net worth pill under the portrait: on for every slot once the match has any.</summary>
    public static readonly StyledProperty<bool> ShowsNetWorthProperty =
        AvaloniaProperty.Register<RosterSlot, bool>(nameof(ShowsNetWorth));

    public static readonly RoutedEvent<HeroEventArgs> RemovedEvent =
        RoutedEvent.Register<RosterSlot, HeroEventArgs>("Removed", RoutingStrategies.Bubble);

    public static readonly RoutedEvent<HeroEventArgs> LaneToggledEvent =
        RoutedEvent.Register<RosterSlot, HeroEventArgs>("LaneToggled", RoutingStrategies.Bubble);

    public static readonly RoutedEvent<HeroEventArgs> RightClickedEvent =
        RoutedEvent.Register<RosterSlot, HeroEventArgs>("RightClicked", RoutingStrategies.Bubble);

    public static readonly RoutedEvent<RoutedEventArgs> EmptyClickedEvent =
        RoutedEvent.Register<RosterSlot, RoutedEventArgs>("EmptyClicked", RoutingStrategies.Bubble);

    private double _portrait = _maxPortrait;
    private bool _overRemove;

    static RosterSlot()
    {
        AffectsRender<RosterSlot>(HeroIdProperty, HeroNameProperty, TeamProperty, IsSelfProperty, InLaneProperty,
            NetWorthProperty, IsPointerOverProperty, ArtHost.ServiceProperty, ArtHost.RevisionProperty);
        AffectsMeasure<RosterSlot>(ShowsNetWorthProperty);
        CursorProperty.OverrideDefaultValue<RosterSlot>(new Cursor(StandardCursorType.Hand));
    }

    public RosterSlot()
    {
        UpdateToolTip();
    }

    public string? HeroId
    {
        get => GetValue(HeroIdProperty);
        set => SetValue(HeroIdProperty, value);
    }

    public string HeroName
    {
        get => GetValue(HeroNameProperty);
        set => SetValue(HeroNameProperty, value);
    }

    public Role Team
    {
        get => GetValue(TeamProperty);
        set => SetValue(TeamProperty, value);
    }

    public bool IsSelf
    {
        get => GetValue(IsSelfProperty);
        set => SetValue(IsSelfProperty, value);
    }

    public bool InLane
    {
        get => GetValue(InLaneProperty);
        set => SetValue(InLaneProperty, value);
    }

    public int? NetWorth
    {
        get => GetValue(NetWorthProperty);
        set => SetValue(NetWorthProperty, value);
    }

    public string? NetWorthChange
    {
        get => GetValue(NetWorthChangeProperty);
        set => SetValue(NetWorthChangeProperty, value);
    }

    public bool ShowsNetWorth
    {
        get => GetValue(ShowsNetWorthProperty);
        set => SetValue(ShowsNetWorthProperty, value);
    }

    public bool IsEmpty => string.IsNullOrEmpty(HeroId);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HeroIdProperty || change.Property == HeroNameProperty || change.Property == TeamProperty
            || change.Property == IsSelfProperty || change.Property == InLaneProperty
            || change.Property == NetWorthProperty || change.Property == NetWorthChangeProperty)
            UpdateToolTip();
    }

    // -- sizing ---------------------------------------------------------------

    /// <summary>Fill the width, capped, and snapped to even sizes.</summary>
    private static double PortraitFor(double width)
    {
        if (double.IsInfinity(width))
            return _maxPortrait;
        var fit = Math.Floor(width - _gap);
        fit -= fit % 2;
        return Math.Clamp(fit, _minPortrait, _maxPortrait);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var portrait = PortraitFor(availableSize.Width);
        return new Size(portrait + _gap, _top + portrait + 3 + PillBand + _nameHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _portrait = PortraitFor(finalSize.Width);
        return finalSize;
    }

    // -- geometry -------------------------------------------------------------

    private Rect PortraitRect => new((Bounds.Width - _portrait) / 2, _top, _portrait, _portrait);

    private double PillBand => ShowsNetWorth ? _pillHeight + 3 : 0;

    internal Rect RemoveBounds
    {
        get
        {
            var portrait = PortraitRect;
            const double diameter = 18;
            const double inset = 3;
            return new Rect(portrait.Right - diameter - inset, portrait.Top + inset, diameter, diameter);
        }
    }

    // -- input ----------------------------------------------------------------

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var overRemove = !IsEmpty && RemoveBounds.Contains(e.GetPosition(this));
        if (overRemove == _overRemove)
            return;
        _overRemove = overRemove;
        UpdateToolTip();
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _overRemove = false;
        UpdateToolTip();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var properties = e.GetCurrentPoint(this).Properties;
        e.Handled = true;

        if (IsEmpty)
        {
            if (properties.IsLeftButtonPressed)
                RaiseEvent(new RoutedEventArgs(EmptyClickedEvent));
            return;
        }

        var heroId = HeroId!;
        if (properties.IsRightButtonPressed)
            RaiseEvent(new HeroEventArgs(RightClickedEvent, heroId));
        else if (properties.IsLeftButtonPressed && RemoveBounds.Contains(e.GetPosition(this)))
            RaiseEvent(new HeroEventArgs(RemovedEvent, heroId));
        // You're always in your own lane.
        else if (properties.IsLeftButtonPressed && !IsSelf)
            RaiseEvent(new HeroEventArgs(LaneToggledEvent, heroId));
    }

    private void UpdateToolTip()
    {
        string tip;
        if (IsEmpty)
            tip = $"Empty slot -- click to add an {Team.Label().ToLowerInvariant()}";
        else if (_overRemove)
            tip = $"Remove {HeroName} from the match";
        else if (IsSelf)
            tip = $"{HeroName} (you)";
        else
            tip = $"{HeroName} -- click to {(InLane ? "take out of" : "put in")} your lane\nRight click for more";
        if (!IsEmpty && !_overRemove && NetWorth is { } souls)
            tip += $"\nNet worth {Format.Compact(souls)}{(NetWorthChange is { } moved ? $" ({moved})" : "")}";
        ToolTip.SetTip(this, tip);
    }

    // -- painting ---------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        // The whole cell takes the pointer, not just what's painted on it.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (IsEmpty)
            PaintEmpty(context);
        else
            PaintHero(context);
    }

    private void PaintEmpty(DrawingContext context)
    {
        var rect = PortraitRect;
        var hovered = IsPointerOver;
        var teamColor = Palette.RoleColor(Team);
        var pen = new Pen(new SolidColorBrush(hovered ? teamColor : Palette.BorderStrong), 1, new DashStyle([4, 2], 0));
        context.DrawRectangle(new SolidColorBrush(hovered ? Palette.Surface3 : Palette.Surface2), pen, new RoundedRect(rect, _radius));
        var plus = Fonts.Text("+", 20, hovered ? teamColor : Palette.TextFaint);
        context.DrawText(plus, new Point(rect.X + (rect.Width - plus.Width) / 2, rect.Y + (rect.Height - plus.Height) / 2));
    }

    private void PaintHero(DrawingContext context)
    {
        var rect = PortraitRect;
        var hovered = IsPointerOver;

        ArtPainter.Draw(context, this, ArtKind.Hero, HeroId!, HeroName, rect, _radius);
        var ring = new Pen(new SolidColorBrush(IsSelf ? Palette.Self : Palette.RoleColor(Team)), hovered ? 3 : 2);
        context.DrawRectangle(null, ring, new RoundedRect(rect, _radius));
        if (InLane)
            LaneBar.Paint(context, rect);

        if (hovered)
        {
            var badge = RemoveBounds;
            var back = Palette.WithAlpha(Palette.Bg, (byte)(_overRemove ? 255 : 200));
            context.DrawEllipse(new SolidColorBrush(back), null, badge);
            var cross = Fonts.Text("×", 13, _overRemove ? Palette.Enemy : Palette.Text, bold: true);
            context.DrawText(cross, new Point(badge.X + (badge.Width - cross.Width) / 2, badge.Y + (badge.Height - cross.Height) / 2));
        }

        if (NetWorth is { } souls)
            PaintNetWorth(context, rect, souls);

        var color = IsSelf || InLane || hovered ? Palette.Text : Palette.TextDim;
        var name = Fonts.Centered(HeroName, 11, color, Bounds.Width, bold: IsSelf);
        context.DrawText(name, new Point(0, rect.Bottom + 3 + PillBand + (_nameHeight - name.Height) / 2));
    }

    /// <summary>The team-coloured pill under the portrait, as the game's top bar draws it.</summary>
    private void PaintNetWorth(DrawingContext context, Rect portrait, int souls)
    {
        var team = Palette.RoleColor(Team);
        var text = Fonts.Text(Format.Compact(souls), 11, Palette.Text, bold: true);
        var width = Math.Min(portrait.Width, Math.Max(portrait.Width * 0.6, text.Width + 12));
        var pill = new Rect(portrait.X + (portrait.Width - width) / 2, portrait.Bottom + 3, width, _pillHeight);
        context.DrawRectangle(new SolidColorBrush(Palette.WithAlpha(team, 70)), new Pen(new SolidColorBrush(team), 1),
            new RoundedRect(pill, 4));
        context.DrawText(text, new Point(pill.X + (pill.Width - text.Width) / 2, pill.Y + (pill.Height - text.Height) / 2));
    }
}
