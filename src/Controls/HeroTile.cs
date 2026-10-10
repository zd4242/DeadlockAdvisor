using Avalonia.Automation.Peers;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using DeadlockAdvisor.Controls.Art;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls;

/// <summary>
/// One hero in the palette: portrait, name, and a team-coloured ring once assigned. Left click,
/// double click and right click bubble up as routed events for the board to act on.
/// </summary>
public class HeroTile : Control
{
    private const double _portrait = 54;
    private const double _pad = 6;
    private const double _nameHeight = 16;
    private const double _radius = 8;

    public static readonly StyledProperty<string> HeroIdProperty =
        AvaloniaProperty.Register<HeroTile, string>(nameof(HeroId), "");

    public static readonly StyledProperty<string> HeroNameProperty =
        AvaloniaProperty.Register<HeroTile, string>(nameof(HeroName), "");

    public static readonly StyledProperty<Role> RoleProperty =
        AvaloniaProperty.Register<HeroTile, Role>(nameof(Role));

    /// <summary>The search box's current pick: Enter would assign this hero.</summary>
    public static readonly StyledProperty<bool> IsHighlightedProperty =
        AvaloniaProperty.Register<HeroTile, bool>(nameof(IsHighlighted));

    public static readonly RoutedEvent<HeroEventArgs> ClickedEvent =
        RoutedEvent.Register<HeroTile, HeroEventArgs>("Clicked", RoutingStrategies.Bubble);

    public static readonly RoutedEvent<HeroEventArgs> DoubleClickedEvent =
        RoutedEvent.Register<HeroTile, HeroEventArgs>("DoubleClicked", RoutingStrategies.Bubble);

    public static readonly RoutedEvent<HeroEventArgs> RightClickedEvent =
        RoutedEvent.Register<HeroTile, HeroEventArgs>("RightClicked", RoutingStrategies.Bubble);

    static HeroTile()
    {
        AffectsRender<HeroTile>(HeroIdProperty, HeroNameProperty, RoleProperty, IsHighlightedProperty,
            IsPointerOverProperty, ArtHost.ServiceProperty, ArtHost.RevisionProperty);
        CursorProperty.OverrideDefaultValue<HeroTile>(new Cursor(StandardCursorType.Hand));
    }

    public string HeroId
    {
        get => GetValue(HeroIdProperty);
        set => SetValue(HeroIdProperty, value);
    }

    public string HeroName
    {
        get => GetValue(HeroNameProperty);
        set => SetValue(HeroNameProperty, value);
    }

    public Role Role
    {
        get => GetValue(RoleProperty);
        set => SetValue(RoleProperty, value);
    }

    public bool IsHighlighted
    {
        get => GetValue(IsHighlightedProperty);
        set => SetValue(IsHighlightedProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new PaintedPeer(this, AutomationControlType.Button,
            () => Role == Role.None ? HeroName : $"{HeroName}, {Role.Label().ToLowerInvariant()}");

    protected override Size MeasureOverride(Size availableSize) =>
        new(_portrait + _pad * 2, _portrait + _pad * 2 + _nameHeight);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
            RaiseEvent(new HeroEventArgs(e.ClickCount == 2 ? DoubleClickedEvent : ClickedEvent, HeroId));
        else if (point.Properties.IsRightButtonPressed)
            RaiseEvent(new HeroEventArgs(RightClickedEvent, HeroId));
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var hovered = IsPointerOver;
        var assigned = Role != Role.None;
        var accent = Palette.RoleColor(Role);
        var rect = new Rect(0.5, 0.5, Bounds.Width - 1, Bounds.Height - 1);

        var background = assigned ? Palette.WithAlpha(accent, 46) : hovered ? Palette.Surface3 : Palette.Surface2;
        context.DrawRectangle(new SolidColorBrush(background), null, new RoundedRect(rect, _radius));

        if (assigned || hovered || IsHighlighted)
        {
            var penColor = assigned ? accent : IsHighlighted ? Palette.Accent : Palette.BorderStrong;
            var pen = new Pen(new SolidColorBrush(penColor), assigned || IsHighlighted ? 2 : 1);
            context.DrawRectangle(null, pen, new RoundedRect(rect, _radius));
        }

        var portrait = new Rect(_pad, _pad, _portrait, _portrait);
        ArtPainter.Draw(context, this, ArtKind.Hero, HeroId, HeroName, portrait, opacity: assigned || hovered ? 1.0 : 0.82);

        var nameWidth = Bounds.Width - 4;
        var name = Fonts.Centered(HeroName, 11, assigned || hovered ? Palette.Text : Palette.TextDim, nameWidth, bold: assigned);
        var nameTop = _pad + _portrait + 1 + (_nameHeight - name.Height) / 2;
        context.DrawText(name, new Point(2, nameTop));
    }
}
