namespace DeadlockAdvisor.Controls;

/// <summary>
/// One team's <see cref="RosterSlot"/>s in a row of equal cells. The row is as wide as the room it's
/// given, up to what its slots can use, so its outer edge follows the window as it resizes instead of
/// moving in steps of whole cells.
/// </summary>
public class RosterRow : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Math.Max(Children.Count, 1);
        var cell = RosterSlot.WidthFor(availableSize.Width / count);
        var height = 0.0;
        foreach (var child in Children)
        {
            child.Measure(new Size(cell, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(cell * count, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cell = finalSize.Width / Math.Max(Children.Count, 1);
        for (var i = 0; i < Children.Count; i++)
            Children[i].Arrange(new Rect(i * cell, 0, cell, finalSize.Height));
        return finalSize;
    }
}
