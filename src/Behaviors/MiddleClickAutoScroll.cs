using System.Diagnostics;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Behaviors;

/// <summary>
/// Browser-style middle-click scrolling for every scroll viewer in a window. A middle press over
/// anything scrollable drops an anchor; the view then scrolls toward the cursor, faster the further
/// it strays. Hold the button and let go to stop, or click without moving to leave it running until
/// the next click, key or wheel: the two modes Chrome and Firefox have.
/// <para>
/// One window-wide handler rather than per-control, because scroll areas are full of children
/// (hero tiles, cards, buttons) that would take a middle press before the scroll viewer saw it.
/// </para>
/// </summary>
public sealed class MiddleClickAutoScroll : IDisposable
{
    /// <summary>Screen DIPs around the anchor that don't scroll at all, at 100% zoom.</summary>
    public const double DeadZone = 12;
    private const double _markerSize = 30;
    private static readonly TimeSpan _tick = TimeSpan.FromMilliseconds(16);

    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private ScrollViewer? _area;
    private (bool Horizontal, bool Vertical) _axes;
    private Point _anchor;
    private Point _pointer;
    private double _zoom = 1;
    private bool _holding;
    private bool _moved;
    private bool _swallowRelease;
    private Canvas? _overlay;

    public MiddleClickAutoScroll(Window window)
    {
        _window = window;
        _timer = new DispatcherTimer(_tick, DispatcherPriority.Render, (_, _) => Tick());
        window.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.Deactivated += OnDeactivated;
    }

    public bool IsScrolling => _area is not null;

    /// <summary>
    /// Pixels per second for a cursor <paramref name="offset"/> px from the anchor: gentle near the
    /// dead zone, quickening with distance so a long list can still be crossed in a second or two.
    /// </summary>
    public static double Speed(double offset, double dead)
    {
        var excess = Math.Abs(offset) - dead;
        if (excess <= 0)
            return 0;
        var speed = excess * 4 + excess * excess * 0.04;
        return offset > 0 ? speed : -speed;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_area is not null)
        {
            // Any click ends a running scroll, and is used up doing so rather than also landing on
            // whatever is under the cursor.
            Stop();
            _swallowRelease = true;
            e.Handled = true;
            return;
        }
        if (e.GetCurrentPoint(_window).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonPressed)
            return;
        if (ScrollAreaAt(e.Source as Visual) is not { } area)
            return;
        Start(area, e.GetPosition(_window));
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_swallowRelease)
        {
            _swallowRelease = false;
            e.Handled = true;
            return;
        }
        if (_area is null || !_holding || e.InitialPressMouseButton != MouseButton.Middle)
            return;
        _holding = false;
        // Held and steered: letting go ends it. A plain click keeps scrolling until the next click.
        if (_moved)
            Stop();
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e) => _pointer = e.GetPosition(_window);

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_area is null)
            return;
        Stop();
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_area is null)
            return;
        Stop();
        if (e.Key == Key.Escape)
            e.Handled = true;
    }

    private void OnDeactivated(object? sender, EventArgs e) => Stop();

    /// <summary>
    /// The nearest scroll viewer with something to scroll. One with no range of its own (a single-line
    /// text box, a short list) passes the press on to whatever holds it, as a browser does.
    /// </summary>
    private static ScrollViewer? ScrollAreaAt(Visual? source)
    {
        if (source is null || source.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null)
            return null;
        return source.GetSelfAndVisualAncestors().OfType<ScrollViewer>().FirstOrDefault(viewer => Axes(viewer) is (true, _) or (_, true));
    }

    private static (bool Horizontal, bool Vertical) Axes(ScrollViewer viewer) =>
        (viewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled && viewer.Extent.Width > viewer.Viewport.Width + 0.5,
         viewer.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled && viewer.Extent.Height > viewer.Viewport.Height + 0.5);

    private void Start(ScrollViewer area, Point anchor)
    {
        _area = area;
        _axes = Axes(area);
        _anchor = anchor;
        _pointer = anchor;
        _holding = true;
        _moved = false;
        _zoom = Math.Abs(area.TransformToVisual(_window)?.M11 ?? 1);

        // A full-window layer over everything: it shows the anchor, carries the scroll cursor (the
        // tiles and buttons underneath each set a cursor of their own), and keeps hover effects off
        // the content sliding past.
        var size = _markerSize * _zoom;
        var marker = new AutoScrollMarker(_axes.Horizontal, _axes.Vertical) { Width = size, Height = size };
        Canvas.SetLeft(marker, anchor.X - size / 2);
        Canvas.SetTop(marker, anchor.Y - size / 2);
        _overlay = new Canvas
        {
            Background = Brushes.Transparent,
            Width = _window.ClientSize.Width,
            Height = _window.ClientSize.Height,
            Cursor = new Cursor(_axes switch
            {
                (true, true) => StandardCursorType.SizeAll,
                (false, true) => StandardCursorType.SizeNorthSouth,
                _ => StandardCursorType.SizeWestEast,
            }),
            Children = { marker },
        };
        OverlayLayer.GetOverlayLayer(_window)?.Children.Add(_overlay);

        _clock.Restart();
        _timer.Start();
    }

    private void Stop()
    {
        if (_area is null)
            return;
        _timer.Stop();
        _area = null;
        if (_overlay is not null)
            OverlayLayer.GetOverlayLayer(_window)?.Children.Remove(_overlay);
        _overlay = null;
    }

    private void Tick()
    {
        var area = _area;
        if (area is null || !area.IsEffectivelyVisible || !area.IsAttachedToVisualTree())
        {
            Stop();
            return;
        }
        // Real elapsed time, so the speed holds even when a tick runs late.
        var elapsed = Math.Min(_clock.Elapsed.TotalSeconds, 0.05);
        _clock.Restart();
        var offset = _pointer - _anchor;
        var dead = DeadZone * _zoom;
        if (Math.Max(Math.Abs(offset.X), Math.Abs(offset.Y)) > dead)
            _moved = true;

        // The cursor moves in window pixels; the content scrolls in its own, before the zoom.
        var dx = _axes.Horizontal ? Speed(offset.X, dead) * elapsed / _zoom : 0;
        var dy = _axes.Vertical ? Speed(offset.Y, dead) * elapsed / _zoom : 0;
        if (dx != 0 || dy != 0)
            area.Offset = new Vector(area.Offset.X + dx, area.Offset.Y + dy);
    }

    public void Dispose()
    {
        Stop();
        _window.Deactivated -= OnDeactivated;
    }
}

/// <summary>The circle left where the middle button went down, with arrows for the directions the area can move.</summary>
internal sealed class AutoScrollMarker(bool horizontal, bool vertical) : Control
{
    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size).Deflate(1);
        var center = rect.Center;
        var size = rect.Width;
        context.DrawEllipse(new SolidColorBrush(Palette.WithAlpha(Palette.Surface2, 230)), new Pen(new SolidColorBrush(Palette.BorderStrong)),
            center, size / 2, size / 2);

        var ink = new SolidColorBrush(Palette.TextDim);
        context.DrawEllipse(ink, null, center, size * 0.07, size * 0.07);

        var directions = new List<(double X, double Y)>();
        if (vertical)
            directions.AddRange([(0, -1), (0, 1)]);
        if (horizontal)
            directions.AddRange([(-1, 0), (1, 0)]);
        var (tip, baseDistance, half) = (size * 0.40, size * 0.24, size * 0.12);
        foreach (var (dx, dy) in directions)
        {
            var baseMid = center + new Point(dx * baseDistance, dy * baseDistance);
            var spread = new Point(-dy * half, dx * half);
            context.DrawGeometry(ink, null, new PolylineGeometry(
                [center + new Point(dx * tip, dy * tip), baseMid + spread, baseMid - spread], isFilled: true));
        }
    }
}
