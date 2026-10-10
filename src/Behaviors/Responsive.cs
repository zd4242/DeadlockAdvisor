namespace DeadlockAdvisor.Behaviors;

/// <summary>
/// Gives a control the <c>narrow</c> class while it is narrower than <see cref="NarrowBelowProperty"/>,
/// so a style can lay its contents out another way: <c>Grid.narrow &gt; TextBlock.note</c>. Sizes are
/// the control's own, in the units its contents are laid out in, so they follow the zoom.
/// <para>
/// Once narrow, it takes eight more units to count as wide again, so a scroll bar appearing in the
/// room the change makes doesn't flip it back and forth.
/// </para>
/// </summary>
public static class Responsive
{
    private const double _hysteresis = 8;

    public static readonly AttachedProperty<double> NarrowBelowProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, double>("NarrowBelow");

    /// <summary>Whether the control is narrow now, for code that has to do something when it changes.</summary>
    public static readonly AttachedProperty<bool> IsNarrowProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, bool>("IsNarrow");

    static Responsive()
    {
        NarrowBelowProperty.Changed.AddClassHandler<Control>((control, change) =>
        {
            control.SizeChanged -= OnSizeChanged;
            if (change.GetNewValue<double>() > 0)
            {
                control.SizeChanged += OnSizeChanged;
                Apply(control, control.Bounds.Width);
            }
            else
            {
                control.SetValue(IsNarrowProperty, false);
            }
        });
        IsNarrowProperty.Changed.AddClassHandler<Control>((control, change) =>
            control.Classes.Set("narrow", change.GetNewValue<bool>()));
    }

    public static double GetNarrowBelow(Control control) => control.GetValue(NarrowBelowProperty);

    public static void SetNarrowBelow(Control control, double value) => control.SetValue(NarrowBelowProperty, value);

    public static bool GetIsNarrow(Control control) => control.GetValue(IsNarrowProperty);

    private static void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Control control && e.WidthChanged)
            Apply(control, e.NewSize.Width);
    }

    private static void Apply(Control control, double width)
    {
        if (width <= 0)
            return;
        var below = GetNarrowBelow(control);
        var narrow = GetIsNarrow(control);
        if (!narrow && width < below)
            control.SetValue(IsNarrowProperty, true);
        else if (narrow && width >= below + _hysteresis)
            control.SetValue(IsNarrowProperty, false);
    }
}
