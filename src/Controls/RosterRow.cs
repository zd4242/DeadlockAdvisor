using Avalonia.VisualTree;

namespace DeadlockAdvisor.Controls;

/// <summary>
/// One team's <see cref="RosterSlot"/>s in a row of equal cells. The row is as wide as the room it's
/// given, up to what its slots can use, so its outer edge follows the window as it resizes instead of
/// moving in steps of whole cells. While a portrait is dragged along it, the row has the cell the hero
/// would land in mark its edge.
/// </summary>
public class RosterRow : Panel
{
    // How far past the row a drag still counts as on it, so overshooting the end hero isn't a miss.
    private const double _dropReach = 24;

    private int _dragFrom = -1;
    private int _dragTo = -1;

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

    // -- drag to reorder -----------------------------------------------------------

    internal RosterSlot? SlotAt(int cell) =>
        cell >= 0 && cell < Children.Count ? Children[cell] as RosterSlot ?? Children[cell].GetVisualDescendants().OfType<RosterSlot>().FirstOrDefault() : null;

    internal int CellOf(RosterSlot slot)
    {
        for (var cell = 0; cell < Children.Count; cell++)
        {
            if (SlotAt(cell) == slot)
                return cell;
        }
        return -1;
    }

    /// <summary>How many cells, from the first, hold a hero; a hero can't be dragged past them.</summary>
    internal int FilledCells
    {
        get
        {
            var filled = 0;
            while (SlotAt(filled) is { IsEmpty: false })
                filled++;
            return filled;
        }
    }

    /// <summary>
    /// The hero-holding cell under <paramref name="point"/> (in the row's own coordinates), the nearest one just past
    /// either end; -1 once the pointer has left the row, which is a drop onto nothing.
    /// </summary>
    internal int CellAt(Point point)
    {
        if (!new Rect(Bounds.Size).Inflate(_dropReach).Contains(point))
            return -1;
        var cell = Bounds.Width / Math.Max(Children.Count, 1);
        return Math.Clamp((int)Math.Floor(point.X / cell), 0, Math.Max(FilledCells - 1, 0));
    }

    /// <summary>Mark where the hero from cell <paramref name="from"/> would land, or nothing for -1.</summary>
    internal void ShowDrop(int from, int to)
    {
        if ((from, to) == (_dragFrom, _dragTo))
            return;
        if (SlotAt(_dragTo) is { } before)
            before.DropEdge = 0;
        (_dragFrom, _dragTo) = (from, to);
        // Moving right the hero ends up after the cell's occupant, moving left before it.
        if (from >= 0 && to >= 0 && from != to && SlotAt(to) is { } target)
            target.DropEdge = to > from ? 1 : -1;
    }
}
