namespace DeadlockAdvisor.Vision.Items;

public enum CellKind
{
    /// <summary>An item: its shop category from the tier tag, and which item when the reader is sure.</summary>
    Item,

    /// <summary>A black cell with a question mark: an item the game doesn't show yet (a Street Brawl's enemy picks, while items are chosen).</summary>
    Hidden,

    /// <summary>A cell that must hold something, since a later one does, but whose tag can't be seen: something is drawn over it.</summary>
    Unseen,
}

/// <param name="Cell">Its place, 0 to 11: row after row, left to right.</param>
/// <param name="Category">From the tag, null for a hidden or unseen cell.</param>
/// <param name="ItemId">Set only when <see cref="Confident"/>.</param>
/// <param name="Score">The best item's match, in the cell's own category and not already taken by the hero.</param>
/// <param name="Margin">How far that is clear of the runner-up.</param>
/// <param name="Tier">The item's tier, 1 to 4: from the item when it is known, else from the numeral printed in the tag; null when neither is clear.</param>
public sealed record CellRead(int Cell, CellKind Kind, ShopCategory? Category, string? ItemId, double Score, double Margin, bool Confident, int? Tier = null)
{
    public bool IsIdentified => ItemId is not null;
}

/// <summary>A hero's three bars, each as the height of its lit part in pitches (0 to <see cref="BarReader.TrackHeight"/>).</summary>
public sealed record BarsRead(double Weapon, double Spirit, double Vitality)
{
    public double Of(ShopCategory category) => category switch
    {
        ShopCategory.Weapon => Weapon,
        ShopCategory.Spirit => Spirit,
        _ => Vitality,
    };
}

/// <param name="Cells">The filled cells in order, which are the leading ones bar the hidden and unseen between them.</param>
/// <param name="Bars">Null when the bars couldn't be read.</param>
/// <param name="Covered">The slot's panel isn't visible (a tooltip or window is over it), so an empty list means nothing.</param>
public sealed record HeroItems(int Slot, IReadOnlyList<CellRead> Cells, BarsRead? Bars, bool Covered)
{
    public int Count => Cells.Count;

    public IEnumerable<CellRead> Identified => Cells.Where(cell => cell.IsIdentified);

    /// <summary>
    /// The souls in a category: every cell of it whose tier is known, at what an item of that tier costs. <c>Exact</c> is false when a
    /// cell of the hero's is hidden, unseen or of no known category or tier, which may hold more.
    /// </summary>
    public (int Souls, bool Exact) Souls(ItemTemplates templates, ShopCategory category)
    {
        var souls = 0;
        var exact = true;
        foreach (var cell in Cells)
        {
            if (cell.Category is null)
                exact = false;
            else if (cell.Category == category && cell.Tier is { } tier && templates.CostOf(category, tier) is { } cost)
                souls += cost;
            else if (cell.Category == category)
                exact = false;
        }
        return (souls, exact);
    }

    /// <summary>Every filled cell's item is known.</summary>
    public bool IsComplete => !Covered && Cells.All(cell => cell.IsIdentified);
}

/// <param name="Souls">What the cells of the category cost, by their tiers.</param>
/// <param name="Exact">No cell that may hold more is unread: <paramref name="Souls"/> is the whole of it.</param>
/// <param name="Expected">The bar's fill (0 to 1) those souls would give.</param>
/// <param name="Read">The bar's fill as read.</param>
public sealed record BarCheck(ShopCategory Category, int Souls, bool Exact, double Expected, double Read, bool Disagrees);

/// <summary>Where the item cells were found, against where the grid said: a slide in pitches and a scale on the pitch.</summary>
/// <param name="SlideLeft">Each team's sideways slide, in the grid's pitches.</param>
/// <param name="Cells">How many cells' tags it lines up with; few, and the grid's own numbers stand.</param>
public sealed record ItemLattice(double SlideLeft, double SlideRight, double SlideY, double Scale, int Cells);

public sealed record ItemBoardRead(IReadOnlyList<HeroItems> Heroes, ItemLattice Lattice);
