using Avalonia.Animation.Easings;
using Avalonia.Input;
using Avalonia.Media;

namespace DeadlockAdvisor.Features.MainWindow;

/// <summary>
/// Keeps the status bar's zoom buttons the size they were when you started pressing them, so the next
/// press lands where the last one did, then eases them to the size the new zoom gives them once the
/// pointer leaves, as a browser's tab strip does after you close a tab. The corner is laid out at its
/// new size at once and only how it's drawn is held back, scaled from the window's corner, so the ease
/// never costs a layout pass.
/// </summary>
internal sealed class ZoomCornerHold
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMilliseconds(180);

    private static readonly Easing _easing = new CubicEaseOut();

    private readonly Control _corner;
    private readonly TimeSpan _duration;
    private readonly ScaleTransform _drawing = new();
    private double _zoom = 1;
    // The zoom the corner is drawn at: the window's, except while held or easing to it.
    private double _drawn = 1;
    private bool _hovered;
    // Which ease is current, so a frame left over from an earlier one does nothing.
    private int _generation;

    public ZoomCornerHold(Control corner, TimeSpan? duration = null)
    {
        _corner = corner;
        _duration = duration ?? DefaultDuration;
        corner.RenderTransformOrigin = RelativePoint.BottomRight;
        corner.RenderTransform = _drawing;
        corner.PointerEntered += (_, _) => Enter();
        corner.PointerExited += (_, _) => Leave();
    }

    /// <summary>The window's zoom is now <paramref name="zoom"/>. With the pointer elsewhere the corner follows at once.</summary>
    public void ZoomChanged(double zoom)
    {
        _zoom = zoom;
        if (!_hovered)
            _drawn = zoom;
        _generation++;
        Draw();
    }

    /// <summary>Holds the size it has reached, even partway through an ease.</summary>
    internal void Enter()
    {
        _hovered = true;
        _generation++;
    }

    internal void Leave()
    {
        _hovered = false;
        if (_drawn != _zoom)
            Ease();
    }

    private void Ease()
    {
        if (TopLevel.GetTopLevel(_corner) is not { } topLevel)
        {
            _drawn = _zoom;
            Draw();
            return;
        }

        var generation = ++_generation;
        var from = _drawn;
        TimeSpan? start = null;

        void OnFrame(TimeSpan now)
        {
            if (generation != _generation)
                return;
            start ??= now;
            var progress = Math.Min(1, (now - start.Value) / _duration);
            _drawn = progress < 1 ? from + (_zoom - from) * _easing.Ease(progress) : _zoom;
            Draw();
            if (progress < 1)
                topLevel.RequestAnimationFrame(OnFrame);
        }

        topLevel.RequestAnimationFrame(OnFrame);
    }

    private void Draw()
    {
        var scale = _drawn / _zoom;
        _drawing.ScaleX = scale;
        _drawing.ScaleY = scale;
    }
}
