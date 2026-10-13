namespace DeadlockAdvisor.Vision.Items;

/// <summary>The shop's three kinds of item, which the tier corner of a cell is coloured by.</summary>
public enum ShopCategory
{
    Weapon,
    Vitality,
    Spirit,
}

/// <summary>
/// Where the scoreboard's item cells and bars sit under each portrait. Every length is in pitches, from the
/// slot's centre across and from the grid's top down, so it holds at any resolution or HUD scale
/// (<c>docs/detection_model.md</c>, "The scoreboard's items").
/// </summary>
public static class ItemGrid
{
    public const int Rows = 6;
    public const int Columns = 2;

    /// <summary>The most a hero holds: twelve cells, six rows of two, filled row by row.</summary>
    public const int CellsPerHero = Rows * Columns;

    public const double LeftColumn = -0.185;
    public const double RightColumn = 0.213;
    public const double FirstRow = 3.82;
    public const double RowStep = 0.396;

    /// <summary>Half a cell's side: the icon fills 0.30 pitches (WP24 measured 0.31 on soft video frames; native captures fit it at exactly 1.00 of this).</summary>
    public const double CellHalf = 0.150;

    /// <summary>
    /// The grid is found from the portraits, so the cells sit a few pixels off the numbers above, the same for every
    /// hero of a board: the reader slides them by up to this much, in pitches, before it looks.
    /// </summary>
    public const double SlideX = 0.05;
    public const double SlideY = 0.06;

    /// <summary>The centre of cell <paramref name="cell"/> (row after row, left to right) for a slot centred at <paramref name="slotCenter"/>.</summary>
    public static (double X, double Y) Centre(Geometry grid, double slotCenter, int cell) =>
        (slotCenter + (cell % Columns == 0 ? LeftColumn : RightColumn) * grid.Pitch,
         grid.Top + (FirstRow + RowStep * (cell / Columns)) * grid.Pitch);

    public static double CellSide(Geometry grid) => 2 * CellHalf * grid.Pitch;

    /// <summary>The bottom of the deepest cell, in pitches from the grid's top: what a capture has to reach.</summary>
    public const double Bottom = FirstRow + RowStep * (Rows - 1) + CellHalf;
}
