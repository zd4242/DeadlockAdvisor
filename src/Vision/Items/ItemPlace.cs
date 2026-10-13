namespace DeadlockAdvisor.Vision.Items;

/// <summary>
/// Where the cells of a board are: the grid's numbers moved by a slide and a scale that make them line up with the tier tags.
/// The portraits give the grid, and it can be a per cent off the HUD in pitch and in the gap between the teams (a Street Brawl's
/// four blank slots leave less to fit) or a few pixels in top, which shows more the deeper a row is, so the cells are fitted
/// on their own tags: one scale and one vertical slide for the board and a sideways slide for each team.
/// </summary>
internal sealed record ItemPlace(Geometry Grid, IReadOnlyList<double> Centers, double SlideLeft, double SlideRight, double SlideY, double Scale)
{
    /// <summary>
    /// Where a tag's window sits in a cell, as shares of its side: the tag is a triangle filling the top right corner, 0.47 of a
    /// side along each edge, and the window is a square inside it, its centre this far in from the corner along both edges.
    /// </summary>
    private const double _tagInset = 0.12;
    private const double _tagHalf = 0.07;

    /// <summary>A slot with this many flooded cell windows is under a colour wash, and is taken as covered.</summary>
    public const int FloodedCovers = 4;

    public const double MinScale = 0.97;
    public const double MaxScale = 1.03;
    private const double _scaleStep = 0.01;
    private const double _slideStepX = 0.0125;
    private const double _slideStepY = 0.01;

    /// <summary>A tag's window is this dense in tag colour to count as one.</summary>
    public const double TagDensity = 0.30;

    /// <summary>The fewest tags a board needs for its fit to be believed; fewer, and the grid's own numbers stand.</summary>
    public const int MinAligned = 8;

    public static ItemPlace Default(Geometry grid, IReadOnlyList<double> centers) => new(grid, centers, 0, 0, 0, 1);

    public double Pitch => Grid.Pitch * Scale;

    public double Top => Grid.Top + SlideY * Grid.Pitch;

    public double Side => 2 * ItemGrid.CellHalf * Pitch;

    public double SlideOf(int slot) => slot < Layout.PerTeam ? SlideLeft : SlideRight;

    public double SlotX(int slot) => Grid.CenterX + (Centers[slot] - Grid.CenterX) * Scale + SlideOf(slot) * Grid.Pitch;

    public (double X, double Y) Cell(int slot, int cell) =>
        (SlotX(slot) + (cell % ItemGrid.Columns == 0 ? ItemGrid.LeftColumn : ItemGrid.RightColumn) * Pitch,
         Top + (ItemGrid.FirstRow + ItemGrid.RowStep * (cell / ItemGrid.Columns)) * Pitch);

    /// <summary>The window to look for a cell's tag in.</summary>
    public (double X, double Y, double Half) TagWindow(int slot, int cell)
    {
        var (x, y) = Cell(slot, cell);
        var offset = (0.5 - _tagInset) * Side;
        return (x + offset, y - offset, _tagHalf * Side);
    }

    /// <summary>How far down the board the cells reach, from the grid's top, with room for the slide and scale.</summary>
    public static double ReachBottom(Geometry grid) => grid.Top + (ItemGrid.Bottom + 0.3) * grid.Pitch;

    public static double ReachTop(Geometry grid) => grid.Top + (ItemGrid.FirstRow - 0.5) * grid.Pitch;

    /// <summary>The slots of a team.</summary>
    public static IEnumerable<int> Team(bool right) => Enumerable.Range(right ? Layout.PerTeam : 0, Layout.PerTeam);

    public ItemPlace WithSlide(bool right, double slide) => right ? this with { SlideRight = slide } : this with { SlideLeft = slide };

    /// <summary>
    /// The places, one for each of the best few scales, with the vertical slide and the slide of each team at which the most cells' tag
    /// windows are full of tag colour (a cell without a tag adds nothing), best first. A scale shows only in the deepest rows and
    /// trades off against the vertical slide, so the tags give a few to choose from, and matching the icons chooses. A board with too
    /// few tags gives the grid's own numbers.
    /// </summary>
    public static (IReadOnlyList<ItemPlace> Candidates, int Cells) Fit(TagMap tags, Geometry grid, IReadOnlyList<double> centers)
    {
        var best = Default(grid, centers);
        var results = new List<(ItemPlace Place, double Score)>();
        for (var scale = MinScale; scale <= MaxScale + 1e-9; scale += _scaleStep)
        {
            var rows = new List<(ItemPlace Place, double Score)>();
            for (var dy = -ItemGrid.SlideY; dy <= ItemGrid.SlideY + 1e-9; dy += _slideStepY)
            {
                var place = Default(grid, centers) with { SlideY = dy, Scale = scale };
                var (left, leftScore) = BestSlide(tags, place, right: false);
                var (both, rightScore) = BestSlide(tags, left, right: true);
                rows.Add((both, leftScore + rightScore));
            }
            // A tag is bigger than the window that looks for it, so a run of slides scores the same: take the middle of the run.
            var peak = rows.Max(row => row.Score);
            var plateau = rows.Where(row => row.Score >= _plateau * peak).ToList();
            var middle = Default(grid, centers) with
            {
                Scale = scale,
                SlideY = plateau.Average(row => row.Place.SlideY),
                SlideLeft = plateau.Average(row => row.Place.SlideLeft),
                SlideRight = plateau.Average(row => row.Place.SlideRight),
            };
            results.Add((middle, peak));
        }
        var leader = results.MaxBy(result => result.Score)!;
        var candidates = results.Where(result => result.Score >= _candidateShare * leader.Score).OrderByDescending(result => result.Score)
            .Take(_candidates).Select(result => result.Place).ToList();
        var cells = leader.Place.Count(tags);
        return cells >= MinAligned ? (candidates, cells) : ([best], cells);
    }

    /// <summary>The scales that line up this share of the best's tag colour are candidates, the best few of them.</summary>
    private const double _candidateShare = 0.9;
    private const int _candidates = 3;

    /// <summary>The sideways slide of one team that lines up the most tag colour, the other settings as they are.</summary>
    private static (ItemPlace Place, double Score) BestSlide(TagMap tags, ItemPlace place, bool right)
    {
        var tries = new List<(double Slide, double Score)>();
        for (var dx = -ItemGrid.SlideX; dx <= ItemGrid.SlideX + 1e-9; dx += _slideStepX)
            tries.Add((dx, place.WithSlide(right, dx).Score(tags, Team(right))));
        var top = tries.Max(attempt => attempt.Score);
        var slide = tries.Where(attempt => attempt.Score >= _plateau * top).Average(attempt => attempt.Slide);
        return (place.WithSlide(right, slide), top);
    }

    /// <summary>The slides that score within this share of the best are one run, and the fit takes its middle.</summary>
    private const double _plateau = 0.98;

    /// <summary>
    /// A tag has its numeral in dark strokes (<see cref="TierReader.InkShare"/>: 0.37 or more at 1440p on the 968 tags measured, 0.29
    /// at 1080p, 0.23 at 900p). A glare or a coloured scene seen through the panel has the colour and none of that (0.01 to 0.08).
    /// </summary>
    public const double MinInk = 0.15;

    /// <summary>Whether a cell's window has a tag in it, with the density of tag colour it has.</summary>
    public (bool IsTag, double Density) Tag(TagMap tags, int slot, int cell)
    {
        var (x, y, half) = TagWindow(slot, cell);
        var density = tags.Density(x, y, half);
        return (density >= TagDensity && TierReader.InkShare(tags.Source, this, slot, cell) >= MinInk, density);
    }

    private double Score(TagMap tags, IEnumerable<int> slots)
    {
        var total = 0.0;
        foreach (var slot in slots)
        {
            for (var cell = 0; cell < ItemGrid.CellsPerHero; cell++)
            {
                if (Tag(tags, slot, cell) is (true, var density))
                    total += density;
            }
        }
        return total;
    }

    /// <summary>How many cells have a tag in their window.</summary>
    public int Count(TagMap tags)
    {
        var cells = 0;
        for (var slot = 0; slot < Centers.Count; slot++)
        {
            for (var cell = 0; cell < ItemGrid.CellsPerHero; cell++)
                cells += Tag(tags, slot, cell).IsTag ? 1 : 0;
        }
        return cells;
    }

    /// <summary>
    /// How many of a slot's cell windows have tag colour and no numeral: a colour wash over the panel (a screen flash, a bright
    /// glare), under which nothing of the slot can be read.
    /// </summary>
    public int Flooded(TagMap tags, int slot)
    {
        var flooded = 0;
        for (var cell = 0; cell < ItemGrid.CellsPerHero; cell++)
        {
            var (isTag, density) = Tag(tags, slot, cell);
            flooded += density >= TagDensity && !isTag ? 1 : 0;
        }
        return flooded;
    }
}
