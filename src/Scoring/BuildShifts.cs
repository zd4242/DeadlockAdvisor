using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Scoring;

/// <summary>A trait's score moved by this many points for the match.</summary>
public readonly record struct TraitShift(string CategoryId, double Points);

/// <summary>
/// What a hero's build does to their damage mix in this match.
/// </summary>
/// <param name="Lean">
/// How much further the items in the hero's cells push toward bullet damage than their usual build does: positive toward bullet, negative
/// toward spirit, up to 2 (every soul in the other direction's items, and their usual build the other way).
/// </param>
/// <param name="Ramp">0 to 1: how much of the effect an inventory of this size carries (<see cref="BuildShifts.FullRampSouls"/>).</param>
/// <param name="Built">The shares of the souls seen in the hero's cells, by shop.</param>
/// <param name="Usual">The shares the hero usually spends, by shop.</param>
public sealed record HeroShift(
    string HeroId,
    IReadOnlyList<TraitShift> Traits,
    double Lean,
    double Ramp,
    int Spent,
    IReadOnlyDictionary<ShopCategory, double> Built,
    IReadOnlyDictionary<ShopCategory, double> Usual)
{
    public double PointsOn(string categoryId) => Traits.FirstOrDefault(trait => trait.CategoryId == categoryId).Points;
}

/// <summary>
/// An enemy (or ally, or you) who has built mostly items that push toward bullet damage is more of a bullet-damage hero in this match, and
/// one who has built spirit items more of a spirit-damage one, so the items that answer each count for more or less against (or with)
/// them. The trait scores are a hero's damage mix at their usual build; a hero is moved off it by how far what their items do leans
/// differently from how their usual build does (<see cref="HeroTypicalBuild"/>). What an item does is its lean (<see cref="ItemLean"/>,
/// <see cref="DataStore.LeanOf"/>): from its stats and the debuffs it puts on enemies, so a spirit item that lowers Bullet Resist counts
/// toward bullet damage, and the shop's colour where the stat table says nothing:
/// <code>
/// built lean = Σ cost × lean(item) ÷ Σ cost, over the cells the scoreboard showed (a cell at its tier's cost)
/// lean       = built lean − the hero's usual lean
/// ramp       = clamp(souls spent ÷ FullRampSouls, 0, 1)
/// bullet    += Alpha × ramp × lean          spirit −= Alpha × ramp × lean   (kept inside the trait's scale)
/// </code>
/// A hero at their usual build moves by 0, and one who always builds the same barely moves; the shift is as likely up as down over
/// matches, so an item still averages 0 (nothing is added to an item that the heroes don't earn). Only a hero whose build the
/// scoreboard has shown, whose usual build the downloaded counts give, and who is rated on the trait moves; with no counts the effect is off.
/// </summary>
public sealed class BuildShifts
{
    public const string BulletTrait = "deals_bullet_damage_general";
    public const string SpiritTrait = "deals_spirit_damage_general";

    /// <summary>The traits a build moves, which the weight matrix keeps a line for.</summary>
    public static readonly IReadOnlyList<string> Traits = [BulletTrait, SpiritTrait];

    /// <summary>
    /// Points of damage share (the traits' scale is a percentage) a hero's mix moves by per unit of lean. Measured from finished matches
    /// with <c>BuildShiftFit</c> (docs/scoring_model.md, "What a hero has built").
    /// </summary>
    public const double Alpha = 36;

    /// <summary>
    /// The souls in the cells at which a hero's inventory counts in full. Their usual build is a whole match's average, so a small early
    /// inventory says little of where the damage will come from.
    /// </summary>
    public const int FullRampSouls = 12000;

    public static readonly BuildShifts None = new([]);

    private readonly Dictionary<string, HeroShift> _byHero;

    private BuildShifts(Dictionary<string, HeroShift> byHero) => _byHero = byHero;

    /// <summary>These heroes' shifts, as they are worked out for a match.</summary>
    public static BuildShifts Of(IEnumerable<HeroShift> shifts)
    {
        var byHero = shifts.ToDictionary(shift => shift.HeroId);
        return byHero.Count == 0 ? None : new BuildShifts(byHero);
    }

    public bool IsEmpty => _byHero.Count == 0;

    public IEnumerable<HeroShift> All => _byHero.Values;

    /// <summary>Null for a hero whose build moves nothing: not read, nothing usual to compare with, not rated, or exactly as usual.</summary>
    public HeroShift? Of(string heroId) => _byHero.GetValueOrDefault(heroId);

    /// <summary>The shift of every hero in the match whose build the scoreboard has shown; none without counts or builds.</summary>
    public static BuildShifts For(DataStore store, MatchState match)
    {
        if (match.Builds.IsEmpty || store.TypicalBuilds.Count == 0)
            return None;
        var scale = CellScale.Of(store);
        var shifts = new List<HeroShift>();
        foreach (var heroId in match.Allies.Concat(match.Enemies).Concat(match.SelfHero is { } self ? [self] : []).Distinct())
        {
            if (match.BuildOf(heroId) is { } build && store.TypicalBuilds.TryGetValue(heroId, out var usual)
                && Shift(store, heroId, build, usual, scale) is { } shift)
                shifts.Add(shift);
        }
        return Of(shifts);
    }

    /// <summary>One hero's shift, or null if their build moves them nowhere.</summary>
    public static HeroShift? Shift(DataStore store, string heroId, HeroBuild build, TypicalBuild usual, CellScale scale)
    {
        if (!store.IsProfiled(heroId))
            return null;
        var souls = Enum.GetValues<ShopCategory>().ToDictionary(category => category, _ => 0.0);
        var leaning = 0.0;
        foreach (var cell in build.Cells.Where(cell => cell.Category is not null))
        {
            var cost = scale.CostOf(cell);
            souls[cell.Category!.Value] += cost;
            leaning += cost * scale.LeanOf(cell);
        }
        var spent = souls.Values.Sum();
        if (spent <= 0)
            return null;

        var built = souls.ToDictionary(pair => pair.Key, pair => pair.Value / spent);
        var lean = leaning / spent - usual.Lean;
        var ramp = Math.Clamp(spent / FullRampSouls, 0.0, 1.0);
        var points = Alpha * ramp * lean;

        var traits = new List<TraitShift>();
        foreach (var (categoryId, direction) in new[] { (BulletTrait, 1.0), (SpiritTrait, -1.0) })
        {
            if (!store.Categories.TryGetValue(categoryId, out var category) || store.HeroScore(heroId, categoryId) is not { } score)
                continue;
            var moved = Math.Clamp(score + direction * points, category.ScaleMin, category.ScaleMax) - score;
            if (Math.Abs(moved) > 1e-9)
                traits.Add(new TraitShift(categoryId, moved));
        }
        return traits.Count == 0 ? null : new HeroShift(heroId, traits, lean, ramp, (int)spent, built, usual.Shares);
    }
}

/// <summary>
/// What a cell of a build counts for. An identified item counts at its tier's cost (or the cell's read tier) and its own lean; a cell
/// whose item wasn't read counts at its shop's median cost where its tier wasn't read either, and at the mean lean of the shop's items of
/// its tier (of all its items where the tier wasn't read).
/// </summary>
public sealed class CellScale
{
    private readonly DataStore _store;
    private readonly Dictionary<ShopCategory, int> _medianCosts = [];
    private readonly Dictionary<(ShopCategory Shop, int Tier), double> _meanLeans = [];
    private readonly Dictionary<ShopCategory, double> _shopLeans = [];

    private CellScale(DataStore store) => _store = store;

    public static CellScale Of(DataStore store)
    {
        var scale = new CellScale(store);
        var shops = store.Items.Values
            .Where(item => item.Tier is >= 1 and <= 4 && ItemTemplates.CategoryOf(item.Category) is not null)
            .GroupBy(item => ItemTemplates.CategoryOf(item.Category)!.Value);
        foreach (var shop in shops)
        {
            var sorted = shop.Select(item => HeroBuild.TierCost(item.Tier)).Order().ToList();
            scale._medianCosts[shop.Key] = sorted[sorted.Count / 2];
            scale._shopLeans[shop.Key] = shop.Average(item => store.LeanOf(item.ItemId).Lean);
            foreach (var tier in shop.GroupBy(item => item.Tier))
                scale._meanLeans[(shop.Key, tier.Key)] = tier.Average(item => store.LeanOf(item.ItemId).Lean);
        }
        return scale;
    }

    /// <summary>The cell's cost; its shop's median cost when its tier wasn't read.</summary>
    public int CostOf(BuildCell cell) => cell.Cost ?? _medianCosts.GetValueOrDefault(cell.Category!.Value);

    /// <summary>The cell's lean: its item's when the item was read, otherwise the mean of what its shop holds at its tier.</summary>
    public double LeanOf(BuildCell cell)
    {
        if (cell.ItemId is { } itemId)
            return _store.LeanOf(itemId).Lean;
        var shop = cell.Category!.Value;
        return cell.Tier is { } tier && _meanLeans.TryGetValue((shop, tier), out var lean) ? lean : _shopLeans.GetValueOrDefault(shop);
    }
}
