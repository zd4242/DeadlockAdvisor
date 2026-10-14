namespace DeadlockAdvisor.Vision.Items;

/// <summary>
/// Reads each hero's items and bars off a capture of the scoreboard (the top 52% of the screen, held Tab), given the grid the
/// portraits were found with. A pure function of the image, the grid and the icons, like <see cref="NetWorthReader"/>, and
/// with the same discipline as hero detection: a cell's identity is only given when it is sure, and unknown is an answer.
/// <list type="number">
/// <item>The tier tags give the cells' places (<see cref="ItemPlace"/>), which are filled, and their category.</item>
/// <item>Each filled cell is matched against the icons of its own category, each item once per hero.</item>
/// <item>The cells that are still unsure are read for the tier in their tag (<see cref="TierReader"/>) and matched again
/// against the items of that tier alone, which settles items drawn alike at different tiers.</item>
/// <item>The bars are read by brightness.</item>
/// </list>
/// </summary>
public static class ItemReader
{
    /// <summary>
    /// A cell's item is given when it matches that well and by that much over the next item of its category (the same kind of
    /// bars the hero reader uses; set on the labelled boards, where no cell read confidently was wrong).
    /// </summary>
    public const double ConfidentScore = 0.70;
    public const double ClearMargin = 0.08;

    /// <summary>
    /// Among the items of the numeral's tier alone, the best of them looks like a match when the true item isn't one with art the game
    /// draws the way the API does (an upgrade drawn as its component, Tankbuster dark), so a match there has to be a better one.
    /// </summary>
    public const double RestrictedScore = 0.75;

    /// <summary>Each cell is read at this many places about where it should be, in pitches, and kept at its best.</summary>
    private static readonly double[] _jitter = [-0.008, 0, 0.008];

    private const int _hiddenSize = 24;
    private const double _hiddenMaxMean = 30;
    private const double _hiddenGlyphLuminance = 100;
    private const double _hiddenMinGlyph = 0.006;
    private const double _hiddenMaxGlyph = 0.12;
    private const double _hiddenMaxEdge = 0.05;

    /// <summary>The cells the refinement looks at, spread over the board, and the fewest of a team's it moves that team on.</summary>
    private const int _referenceCells = 36;
    private const int _minReferenceCells = 3;

    /// <summary>The least <see cref="PanelSpread"/> of a panel that is showing.</summary>
    internal const double MinPanelSpread = 4.0;

    /// <param name="skipSlots">Slots to leave out: a Street Brawl's blank ones.</param>
    public static ItemBoardRead Read(RgbImage band, Geometry grid, ItemTemplates templates, IReadOnlyCollection<int>? skipSlots = null)
    {
        var centers = grid.Centers();
        var tags = TagMap.Build(band, 0, (int)Math.Floor(ItemPlace.ReachTop(grid)), band.Width, (int)Math.Ceiling(ItemPlace.ReachBottom(grid)));
        var (candidates, aligned) = ItemPlace.Fit(tags, grid, centers);
        var (place, bestScore) = (candidates[0], double.MinValue);
        foreach (var candidate in candidates)
        {
            var (refined, score) = Refine(band, tags, candidate, templates, templates.FramesAt((int)Math.Round(candidate.Side)));
            if (score > bestScore)
                (place, bestScore) = (refined, score);
        }
        var frames = templates.FramesAt((int)Math.Round(place.Side));

        var slots = Enumerable.Range(0, centers.Count).Where(slot => skipSlots?.Contains(slot) != true).ToList();
        var passes = new HeroPass[slots.Count];
        Parallel.For(0, slots.Count, index => passes[index] = FirstPass(band, tags, place, templates, frames, slots[index]));

        var tiers = TiersOf(band, place, passes);
        for (var round = 0; round < _vetoRounds && Veto(band, place, tiers, passes) > 0; round++)
            tiers = TiersOf(band, place, passes);
        var heroes = new HeroItems[slots.Count];
        Parallel.For(0, slots.Count, index => heroes[index] = SecondPass(band, place, templates, tiers, passes[index]));
        return new ItemBoardRead(heroes, new ItemLattice(place.SlideLeft, place.SlideRight, place.SlideY, place.Scale, aligned));
    }

    private const int _vetoRounds = 2;

    /// <summary>How far an item of another tier may match better than the best of the numeral's tier, for that to be confident.</summary>
    private const double _tierSlack = 0.05;

    /// <summary>The numerals of the cells the icons identified, each as its item's tier: what the board's other numerals are read against.</summary>
    private static TierReader TiersOf(RgbImage band, ItemPlace place, HeroPass[] passes)
    {
        var tiers = new TierReader();
        foreach (var pass in passes)
        {
            foreach (var cell in pass.Cells.Where(cell => cell.Tier is not null && cell.Confident))
                tiers.Add(cell.Tier!.Value, TierReader.Read(band, place, pass.Slot, cell.Cell), pass.Slot, cell.Cell);
        }
        return tiers;
    }

    /// <summary>
    /// An item read confidently whose numeral clearly says another tier is one or the other wrong, most likely the item (an
    /// upgrade drawn like what it upgrades from), so it is made unsure and keeps the numeral's tier. Returns how many.
    /// </summary>
    private static int Veto(RgbImage band, ItemPlace place, TierReader tiers, HeroPass[] passes)
    {
        var vetoed = 0;
        for (var i = 0; i < passes.Length; i++)
        {
            var pass = passes[i];
            var changed = pass.Cells.ToList();
            for (var at = 0; at < changed.Count; at++)
            {
                var cell = changed[at];
                if (!cell.Confident || cell.Tier is not { } tier
                    || tiers.Contradicts(TierReader.Read(band, place, pass.Slot, cell.Cell), tier, pass.Slot, cell.Cell) is not { } seen)
                    continue;
                changed[at] = cell with { ItemId = null, Confident = false, Tier = seen };
                vetoed++;
            }
            passes[i] = pass with { Cells = changed };
        }
        return vetoed;
    }

    /// <param name="Scores">For each cell that holds an item, every item's match, -1 for the other categories'.</param>
    private sealed record HeroPass(int Slot, bool Covered, IReadOnlyList<CellRead> Cells, IReadOnlyDictionary<int, float[]> Scores, BarsRead? Bars);

    /// <summary>
    /// The tags place the cells to a pixel or so, and an icon only matches well when it is within about that: slide the cells to
    /// where a spread of tagged ones match their category's icons best, the vertical slide for the board and a sideways one for
    /// each team.
    /// </summary>
    private static (ItemPlace Place, double Score) Refine(RgbImage band, TagMap tags, ItemPlace place, ItemTemplates templates, float[][] frames)
    {
        var tagged = new List<(int Slot, int Cell, ShopCategory Category)>();
        for (var slot = 0; slot < place.Centers.Count; slot++)
        {
            for (var cell = 0; cell < ItemGrid.CellsPerHero; cell++)
            {
                var (x, y, half) = place.TagWindow(slot, cell);
                if (place.Tag(tags, slot, cell).IsTag && tags.Dominant(x, y, half) is ({ } category, _))
                    tagged.Add((slot, cell, category));
            }
        }
        var step = Math.Max(1, tagged.Count / _referenceCells);
        var reference = tagged.Where((_, i) => i % step == 0).ToList();
        var left = reference.Where(cell => cell.Slot < Layout.PerTeam).ToList();
        var right = reference.Where(cell => cell.Slot >= Layout.PerTeam).ToList();
        if (left.Count < _minReferenceCells && right.Count < _minReferenceCells)
            return (place, 0);

        double Sum(ItemPlace trial, List<(int Slot, int Cell, ShopCategory Category)> cells)
        {
            var total = 0.0;
            foreach (var (slot, cell, category) in cells)
            {
                var (x, y) = trial.Cell(slot, cell);
                var vector = ItemFrame.Cell(band, x, y, trial.Side);
                var best = -1f;
                foreach (var icon in templates.IconsIn(category))
                    best = Math.Max(best, TemplateBank.Dot(vector, frames[icon]));
                total += best;
            }
            return total;
        }

        var current = place;
        var lastTotal = 0.0;
        foreach (var (reach, stride) in new[] { (0.03, 0.01), (0.008, 0.004) })
        {
            var centre = current;
            var (bestTotal, best) = (double.MinValue, current);
            for (var dy = -reach; dy <= reach + 1e-9; dy += stride)
            {
                var row = centre with { SlideY = centre.SlideY + dy };
                var moved = row;
                var total = 0.0;
                foreach (var (isRight, cells) in new[] { (false, left), (true, right) })
                {
                    if (cells.Count < _minReferenceCells)
                        continue;
                    var first = isRight ? Layout.PerTeam : 0;
                    var (sideBest, sideSlide) = (double.MinValue, row.SlideOf(first));
                    for (var dx = -reach; dx <= reach + 1e-9; dx += stride)
                    {
                        var slide = row.SlideOf(first) + dx;
                        var score = Sum(row.WithSlide(isRight, slide), cells);
                        if (score > sideBest)
                            (sideBest, sideSlide) = (score, slide);
                    }
                    moved = moved.WithSlide(isRight, sideSlide);
                    total += sideBest;
                }
                if (total > bestTotal)
                    (bestTotal, best) = (total, moved);
            }
            (current, lastTotal) = (best, bestTotal);
        }
        var counted = (left.Count >= _minReferenceCells ? left.Count : 0) + (right.Count >= _minReferenceCells ? right.Count : 0);
        return (current, lastTotal / Math.Max(1, counted));
    }

    private static HeroPass FirstPass(RgbImage band, TagMap tags, ItemPlace place, ItemTemplates templates, float[][] frames, int slot)
    {
        if (PanelSpread(band, place, slot) < MinPanelSpread || place.Flooded(tags, slot) >= ItemPlace.FloodedCovers)
            return new HeroPass(slot, true, [], new Dictionary<int, float[]>(), null);

        var kinds = new CellKind?[ItemGrid.CellsPerHero];
        var categories = new ShopCategory?[ItemGrid.CellsPerHero];
        var last = -1;
        for (var cell = 0; cell < kinds.Length; cell++)
        {
            var (x, y, half) = place.TagWindow(slot, cell);
            if (place.Tag(tags, slot, cell).IsTag && tags.Dominant(x, y, half) is ({ } category, _))
            {
                (kinds[cell], categories[cell]) = (CellKind.Item, category);
                last = cell;
            }
            else if (IsHidden(band, place, slot, cell))
            {
                kinds[cell] = CellKind.Hidden;
                last = cell;
            }
        }

        last = PlausibleEnd(kinds, last);
        var cells = new List<CellRead>();
        var scores = new Dictionary<int, float[]>();
        for (var cell = 0; cell <= last; cell++)
        {
            if (kinds[cell] is not { } kind)
                cells.Add(new CellRead(cell, CellKind.Unseen, null, null, 0, 0, false));
            else if (kind == CellKind.Hidden)
                cells.Add(new CellRead(cell, CellKind.Hidden, null, null, 0, 0, false));
            else
                scores[cell] = ScoreCell(band, place, templates, frames, slot, cell, categories[cell]!.Value);
        }

        cells.AddRange(Assign(templates, scores.Select(pair => (pair.Key, pair.Value)).ToList(), categories, [], tier: null));
        return new HeroPass(slot, false, cells.OrderBy(read => read.Cell).ToList(), scores, BarReader.Read(band, place.Top, place.Pitch, place.SlotX(slot)));
    }

    /// <summary>The cells the icons left unsure get their tier from the numeral and, with one, another look among that tier's items.</summary>
    private static HeroItems SecondPass(RgbImage band, ItemPlace place, ItemTemplates templates, TierReader tiers, HeroPass pass)
    {
        if (pass.Covered)
            return new HeroItems(pass.Slot, [], null, Covered: true);

        var cells = pass.Cells.ToDictionary(cell => cell.Cell);
        var taken = pass.Cells.Where(cell => cell.IsIdentified).Select(cell => templates.IndexOf(cell.ItemId!)!.Value).ToHashSet();
        var open = new List<(int Cell, float[] Scores)>();
        var tierOf = new Dictionary<int, int>();
        foreach (var cell in pass.Cells.Where(cell => cell.Kind == CellKind.Item))
        {
            if (cell.IsIdentified || tiers.ReferenceCount == 0)
                continue;
            if (tiers.Classify(TierReader.Read(band, place, pass.Slot, cell.Cell)) is { } tier)
            {
                tierOf[cell.Cell] = tier;
                open.Add((cell.Cell, pass.Scores[cell.Cell]));
            }
        }

        foreach (var cell in pass.Cells.Where(cell => cell.IsIdentified))
            cells[cell.Cell] = cell with { Tier = templates.Items[templates.IndexOf(cell.ItemId!)!.Value].Tier };
        foreach (var (cell, tier) in tierOf)
            cells[cell] = cells[cell] with { Tier = tier };
        foreach (var read in Assign(templates, open, pass.Cells.ToDictionary(cell => cell.Cell, cell => cell.Category), taken, tierOf))
            cells[read.Cell] = read;
        return new HeroItems(pass.Slot, cells.Values.OrderBy(cell => cell.Cell).ToList(), pass.Bars, Covered: false);
    }

    /// <summary>An item can be owned once, so the cell that matches an item best keeps it, then the next best of the rest.</summary>
    /// <param name="tier">Limits a cell to the items of its tier, when the numeral said which it is.</param>
    private static List<CellRead> Assign(ItemTemplates templates, List<(int Cell, float[] Scores)> scored, ShopCategory?[] categories,
        HashSet<int> taken, IReadOnlyDictionary<int, int>? tier) =>
        Assign(templates, scored, categories.Select((category, cell) => (category, cell)).ToDictionary(pair => pair.cell, pair => pair.category), taken, tier);

    private static List<CellRead> Assign(ItemTemplates templates, List<(int Cell, float[] Scores)> scored, IReadOnlyDictionary<int, ShopCategory?> categories,
        HashSet<int> taken, IReadOnlyDictionary<int, int>? tier)
    {
        var reads = new List<CellRead>();
        taken = [.. taken];
        var open = scored.ToList();
        float Of((int Cell, float[] Scores) candidate, int item) =>
            taken.Contains(item) || (tier is not null && tier.TryGetValue(candidate.Cell, out var wanted) && templates.Items[item].Tier != wanted)
                ? -1f
                : candidate.Scores[item];
        while (open.Count > 0)
        {
            var (cell, item, score) = (-1, -1, float.MinValue);
            foreach (var candidate in open)
            {
                for (var i = 0; i < candidate.Scores.Length; i++)
                {
                    if (Of(candidate, i) > score)
                        (cell, item, score) = (candidate.Cell, i, Of(candidate, i));
                }
            }
            if (item < 0 || score < 0)
                break;
            var chosen = open.First(candidate => candidate.Cell == cell);
            taken.Add(item);
            open.Remove(chosen);
            var runnerUp = Enumerable.Range(0, chosen.Scores.Length).Where(i => i != item).Select(i => Of(chosen, i)).DefaultIfEmpty(-1f).Max();
            var margin = score - runnerUp;
            var restricted = tier is not null && tier.ContainsKey(cell);
            var confident = score >= (restricted ? RestrictedScore : ConfidentScore) && margin >= ClearMargin;
            if (confident && tier is not null && tier.TryGetValue(cell, out var cellTier))
            {
                // Among the items of the numeral's tier alone, the best of them looks like a match whatever the true item is, if its own art
                // isn't what the game draws. An item of another tier that matches clearly better says that is the case here.
                var otherTier = Enumerable.Range(0, chosen.Scores.Length).Where(i => templates.Items[i].Tier != cellTier).Select(i => chosen.Scores[i]).DefaultIfEmpty(-1f).Max();
                confident = score >= otherTier - _tierSlack;
            }
            var tierRead = tier is not null && tier.TryGetValue(cell, out var wantedTier) ? wantedTier : (int?)null;
            reads.Add(new CellRead(cell, CellKind.Item, categories[cell], confident ? templates.Items[item].ItemId : null, score, margin, confident,
                confident ? templates.Items[item].Tier : tierRead));
        }
        foreach (var (cell, _) in open)
            reads.Add(new CellRead(cell, CellKind.Item, categories[cell], null, 0, 0, false, tier is not null && tier.TryGetValue(cell, out var t) ? t : null));
        return reads;
    }

    /// <summary>Every item's match for the cell, at its best over the little jitter; items of the other categories score -1.</summary>
    internal static float[] ScoreCell(RgbImage band, ItemPlace place, ItemTemplates templates, float[][] frames, int slot, int cell, ShopCategory category)
    {
        var (x, y) = place.Cell(slot, cell);
        var scores = new float[templates.Items.Count];
        Array.Fill(scores, -1f);
        foreach (var jx in _jitter)
        {
            foreach (var jy in _jitter)
            {
                var vector = ItemFrame.Cell(band, x + jx * place.Pitch, y + jy * place.Pitch, place.Side);
                foreach (var icon in templates.IconsIn(category))
                {
                    var item = templates.ItemOfIcon[icon];
                    scores[item] = Math.Max(scores[item], TemplateBank.Dot(vector, frames[icon]));
                }
            }
        }
        return scores;
    }

    /// <summary>
    /// Cells fill in order, so a tag far past the others, with nothing before it, is a stray colour in the scene rather than an
    /// item. Something drawn over a few cells hides a few tags, never most of a hero's: the last cell is the furthest one with at
    /// least as many seen cells (tags, or hidden ones) up to it as unseen.
    /// </summary>
    private static int PlausibleEnd(CellKind?[] kinds, int last)
    {
        int seen = 0, unseen = 0, gap = 0, end = -1;
        for (var cell = 0; cell <= last; cell++)
        {
            if (kinds[cell] is null)
            {
                unseen++;
                gap += seen > 0 ? 1 : 0;
                continue;
            }
            if (gap > _maxGap)
                break;
            (seen, gap) = (seen + 1, 0);
            if (unseen <= seen)
                end = cell;
        }
        return end;
    }

    /// <summary>
    /// What hides a few cells (the kill feed, a toast) covers the first rows or whole columns, not a run of them in the middle of a
    /// hero's items: more than this many unseen cells between two seen ones, and the second isn't one of the hero's.
    /// </summary>
    private const int _maxGap = 2;

    /// <summary>
    /// How much a slot's panel varies where the bars are, the standard deviation of luminance over a band across them. A panel
    /// with a tooltip or a window over it is flat there (2 to 3 on the boards measured, against 5 and up for the least
    /// varied real panel, a bar track at the start of a match), and what shows is nothing of the slot's.
    /// </summary>
    internal static double PanelSpread(RgbImage band, ItemPlace place, int slot)
    {
        var x = place.SlotX(slot);
        int x0 = Math.Max(0, (int)Math.Round(x - 0.2 * place.Pitch)), x1 = Math.Min(band.Width, (int)Math.Round(x + 0.2 * place.Pitch));
        int y0 = Math.Max(0, (int)Math.Round(place.Top + 2.17 * place.Pitch)), y1 = Math.Min(band.Height, (int)Math.Round(place.Top + 2.44 * place.Pitch));
        double total = 0, squares = 0;
        var count = 0;
        for (var py = y0; py < y1; py++)
        {
            for (var px = x0; px < x1; px++)
            {
                var (r, g, b) = band[px, py];
                var luminance = 0.299 * r + 0.587 * g + 0.114 * b;
                total += luminance;
                squares += luminance * luminance;
                count++;
            }
        }
        if (count == 0)
            return 0;
        var mean = total / count;
        return Math.Sqrt(Math.Max(0, squares / count - mean * mean));
    }

    /// <summary>The mean luminance of a cell, the share of it that is bright, and the share of the bright that is in its outer fifth, over its whole area.</summary>
    internal static (double Mean, double Bright, double Edge) Glyph(RgbImage band, ItemPlace place, int slot, int cell)
    {
        var (x, y) = place.Cell(slot, cell);
        if (x - place.Side / 2 < 0 || y - place.Side / 2 < 0 || x + place.Side / 2 > band.Width || y + place.Side / 2 > band.Height)
            return (double.NaN, double.NaN, double.NaN);
        var sample = ItemFrame.Sample(band, x, y, place.Side, _hiddenSize);
        double total = 0;
        int bright = 0, edge = 0;
        var cells = _hiddenSize * _hiddenSize;
        for (var i = 0; i < cells; i++)
        {
            var luminance = 0.299 * sample.Pixels[i * 3] + 0.587 * sample.Pixels[i * 3 + 1] + 0.114 * sample.Pixels[i * 3 + 2];
            total += luminance;
            if (luminance < _hiddenGlyphLuminance)
                continue;
            bright++;
            var (column, row) = (i % _hiddenSize, i / _hiddenSize);
            edge += Math.Min(Math.Min(column, row), Math.Min(_hiddenSize - 1 - column, _hiddenSize - 1 - row)) < _hiddenSize / 5 ? 1 : 0;
        }
        return (total / cells, (double)bright / cells, bright == 0 ? 0 : (double)edge / bright);
    }

    /// <summary>
    /// A black cell with a white mark in the middle: the question mark the game draws for an item it keeps hidden. Black with light
    /// reaching its edge is something else drawn over the panel, such as the pause banner's lettering under a short panel.
    /// </summary>
    private static bool IsHidden(RgbImage band, ItemPlace place, int slot, int cell)
    {
        var (mean, bright, edge) = Glyph(band, place, slot, cell);
        return mean <= _hiddenMaxMean && bright is >= _hiddenMinGlyph and <= _hiddenMaxGlyph && edge <= _hiddenMaxEdge;
    }
}
