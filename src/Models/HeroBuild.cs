using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Models;

/// <summary>One of a hero's item cells as the scoreboard showed it.</summary>
/// <param name="Cell">Its place, 0 to 11: row after row, left to right.</param>
/// <param name="Category">Null for a hidden or unseen cell.</param>
/// <param name="ItemId">Set only when the reader was sure.</param>
/// <param name="Tier">1 to 4, from the item or the numeral on its tag; null when neither was clear.</param>
public sealed record BuildCell(int Cell, CellKind Kind, ShopCategory? Category, string? ItemId, int? Tier)
{
    public bool IsIdentified => ItemId is not null;

    /// <summary>Every item costs 800 souls at tier 1, and twice as much at each tier after.</summary>
    public int? Cost => Tier is >= 1 and <= 4 ? HeroBuild.TierCost(Tier.Value) : null;

    public static BuildCell Of(CellRead read) => new(read.Cell, read.Kind, read.Category, read.ItemId, read.Tier);
}

/// <param name="Souls">What the cells of the category cost, or what its bar says if that is more.</param>
/// <param name="IsEstimate">Some cell may hold more than was seen, so the figure is a least (or the bar's reckoning), not the whole.</param>
public readonly record struct BuildInvestment(ShopCategory Category, int Souls, bool IsEstimate);

/// <summary>
/// What one hero has built, as far as the scoreboard has shown it: their item cells, their three bars and when it was last read.
/// A read says nothing about the cells it couldn't see (a covered slot, a cell under the kill feed, a Street Brawl's hidden pick, the
/// rows past a tooltip), so <see cref="Merge"/> adds it to what was known rather than replacing it.
/// </summary>
public sealed class HeroBuild(IReadOnlyList<BuildCell> cells, BarsRead? bars, DateTimeOffset at)
{
    public const int TierOneCost = 800;

    /// <summary>What an item of this tier counts for in a build, whatever the shop charges: 800, 1600, 3200, 6400.</summary>
    public static int TierCost(int tier) => TierOneCost << (tier - 1);

    /// <summary>A bar this full is the same as one about 28k souls up; the souls a bar implies stop there.</summary>
    private const double _fullestBar = 0.95;

    public IReadOnlyList<BuildCell> Cells { get; } = cells;

    /// <summary>Null when the bars couldn't be read.</summary>
    public BarsRead? Bars { get; } = bars;

    public DateTimeOffset At { get; } = at;

    public IEnumerable<string> ItemIds => Cells.Where(cell => cell.IsIdentified).Select(cell => cell.ItemId!);

    public bool Owns(string itemId) => Cells.Any(cell => cell.ItemId == itemId);

    public BuildInvestment InvestmentIn(ShopCategory category)
    {
        var souls = 0;
        var estimate = Cells.Any(cell => cell.Category is null);
        foreach (var cell in Cells.Where(cell => cell.Category == category))
        {
            if (cell.Cost is { } cost)
                souls += cost;
            else
                estimate = true;
        }
        if (estimate && Bars is { } bars)
        {
            var fill = Math.Min(_fullestBar, bars.Of(category) / BarReader.TrackHeight);
            souls = Math.Max(souls, (int)Math.Round(-10000 * Math.Log(1 - fill)));
        }
        return new BuildInvestment(category, souls, estimate);
    }

    /// <summary>How the hero's souls divide among the three categories, as shares of one; all zero when nothing is invested.</summary>
    public IReadOnlyDictionary<ShopCategory, double> Shares()
    {
        var investments = Enum.GetValues<ShopCategory>().Select(InvestmentIn).ToList();
        var total = investments.Sum(investment => investment.Souls);
        return investments.ToDictionary(investment => investment.Category, investment => total > 0 ? (double)investment.Souls / total : 0);
    }

    public SavedBuild ToSaved() => new()
    {
        At = At,
        Cells = Cells.Select(cell => new SavedBuildCell
        {
            Cell = cell.Cell,
            Kind = cell.Kind.ToString().ToLowerInvariant(),
            Category = cell.Category?.ToString().ToLowerInvariant(),
            ItemId = cell.ItemId,
            Tier = cell.Tier,
        }).ToList(),
        Bars = Bars is { } bars ? new SavedBars { Weapon = bars.Weapon, Vitality = bars.Vitality, Spirit = bars.Spirit } : null,
    };

    /// <summary>A saved build, quietly dropping what no longer makes sense: a cell out of range or of no category, an item the shop no longer has (the cell keeps its tier).</summary>
    public static HeroBuild FromSaved(SavedBuild saved, Func<string, bool> knowsItem)
    {
        var cells = new Dictionary<int, BuildCell>();
        foreach (var cell in saved.Cells.Where(cell => cell.Cell is >= 0 and < ItemGrid.CellsPerHero))
        {
            var kind = Enum.TryParse<CellKind>(cell.Kind, ignoreCase: true, out var parsed) ? parsed : CellKind.Item;
            var category = cell.Category is null ? null : ItemTemplates.CategoryOf(cell.Category);
            if (kind == CellKind.Item && category is null)
                continue;
            var itemId = cell.ItemId is { } id && knowsItem(id) ? id : null;
            cells[cell.Cell] = new BuildCell(cell.Cell, kind, kind == CellKind.Item ? category : null, kind == CellKind.Item ? itemId : null,
                cell.Tier is >= 1 and <= 4 ? cell.Tier : null);
        }
        var bars = saved.Bars is { } b ? new BarsRead(b.Weapon, b.Spirit, b.Vitality) : null;
        return new HeroBuild([.. cells.Values.OrderBy(cell => cell.Cell)], bars, saved.At);
    }

    /// <summary>
    /// A read added to what was known. Only the cells the read saw change; a cell it saw as an item it is unsure of keeps the item it
    /// was known as, when the category and tier agree, and an item can be held once, so a cell read as an item takes it from any other.
    /// A covered slot, or one that read as nothing, changes nothing.
    /// </summary>
    public static HeroBuild? Merge(HeroBuild? known, HeroItems read, DateTimeOffset at)
    {
        if (read.Covered || (read.Cells.Count == 0 && read.Bars is null))
            return known;

        var before = known?.Cells.ToDictionary(cell => cell.Cell) ?? [];
        var cells = new Dictionary<int, BuildCell>(before);
        foreach (var seen in read.Cells)
        {
            var now = BuildCell.Of(seen);
            before.TryGetValue(seen.Cell, out var was);
            cells[seen.Cell] = seen.Kind switch
            {
                CellKind.Item when now.IsIdentified => now,
                CellKind.Item when was is { IsIdentified: true } && was.Category == now.Category && (now.Tier is null || now.Tier == was.Tier) => was,
                CellKind.Item => now,
                _ => was ?? now,
            };
        }

        var placed = read.Cells.Where(cell => cell.IsIdentified).ToDictionary(cell => cell.ItemId!, cell => cell.Cell);
        foreach (var (cell, build) in cells.ToList())
        {
            if (build.ItemId is { } id && placed.TryGetValue(id, out var now) && now != cell)
                cells[cell] = build with { ItemId = null };
        }

        return new HeroBuild([.. cells.Values.OrderBy(cell => cell.Cell)], read.Bars ?? known?.Bars, at);
    }
}

/// <summary>
/// The latest build of each hero in the match. Kept with the match, saved and restored with it, kept across a re-detect of the same
/// twelve, and dropped for a hero who leaves.
/// </summary>
public sealed class MatchBuilds
{
    private readonly Dictionary<string, HeroBuild> _builds = [];

    public IReadOnlyDictionary<string, HeroBuild> All => _builds;

    public bool IsEmpty => _builds.Count == 0;

    public DateTimeOffset? LatestAt => _builds.Count == 0 ? null : _builds.Values.Max(build => build.At);

    public HeroBuild? Of(string? heroId) => heroId is not null && _builds.TryGetValue(heroId, out var build) ? build : null;

    public void Set(string heroId, HeroBuild build) => _builds[heroId] = build;

    /// <summary>Add a read of a hero's items to what is known of them. Returns whether the hero's build changed.</summary>
    public bool Apply(string heroId, HeroItems read, DateTimeOffset at)
    {
        var known = Of(heroId);
        var merged = HeroBuild.Merge(known, read, at);
        if (merged is null || ReferenceEquals(merged, known))
            return false;
        _builds[heroId] = merged;
        return true;
    }

    public void Clear() => _builds.Clear();

    /// <summary>The builds of <paramref name="heroIds"/>, as saved with the match.</summary>
    public Dictionary<string, SavedBuild> ToSaved(IEnumerable<string> heroIds) =>
        heroIds.Where(_builds.ContainsKey).Distinct().ToDictionary(heroId => heroId, heroId => _builds[heroId].ToSaved());
}
