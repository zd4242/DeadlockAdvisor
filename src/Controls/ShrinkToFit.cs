using Avalonia.Media;

namespace DeadlockAdvisor.Controls;

/// <summary>
/// Shows its child at full size, or scaled down, to no less than <see cref="MinScale"/>, when the
/// room it is given is shorter than the child wants. Below that scale the child gets the room
/// it has left, to scroll in. Popups inside take the scale with them.
/// </summary>
public class ShrinkToFit : Decorator
{
    public static readonly StyledProperty<double> MinScaleProperty =
        AvaloniaProperty.Register<ShrinkToFit, double>(nameof(MinScale), 1);

    private double _scale = 1;

    static ShrinkToFit()
    {
        AffectsMeasure<ShrinkToFit>(MinScaleProperty);
    }

    public ShrinkToFit()
    {
        ClipToBounds = true;
    }

    /// <summary>The smallest scale to shrink to; 1 never shrinks.</summary>
    public double MinScale
    {
        get => GetValue(MinScaleProperty);
        set => SetValue(MinScaleProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _scale = 1;
        if (Child is not { } child)
            return default;

        if (MinScale >= 1 || double.IsInfinity(availableSize.Height))
        {
            child.Measure(availableSize);
            return child.DesiredSize;
        }

        child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var wanted = child.DesiredSize.Height;
        if (wanted > availableSize.Height)
            _scale = Math.Max(MinScale, availableSize.Height / wanted);

        // The extra pixel keeps rounding from tipping a child that fits into scrolling.
        child.Measure(new Size(availableSize.Width / _scale, availableSize.Height / _scale + 1));
        return new Size(child.DesiredSize.Width * _scale, Math.Min(child.DesiredSize.Height * _scale, availableSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not { } child)
            return finalSize;

        child.RenderTransformOrigin = RelativePoint.TopLeft;
        child.RenderTransform = _scale < 1 ? new ScaleTransform(_scale, _scale) : null;
        child.Arrange(new Rect(0, 0, finalSize.Width / _scale, finalSize.Height / _scale));
        return finalSize;
    }
}
