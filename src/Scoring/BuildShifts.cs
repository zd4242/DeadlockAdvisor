using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Scoring;

/// <summary>A trait's score moved by this many points for the match.</summary>
public readonly record struct TraitShift(string CategoryId, double Points);

/// <summary>
/// What a hero's build does to their damage mix in this match.
/// </summary>
/// <param name="Lean">How much further their souls went to weapon items rather than spirit ones than they usually do: positive for weapon, negative for spirit, 1 when the whole of an even split moved.</param>
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
/// An enemy (or ally, or you) who has built mostly weapon items is more of a bullet-damage hero in this match, and one who has built
/// spirit items more of a spirit-damage one, so the items that answer each count for more or less against (or with) them. The trait
/// scores are a hero's damage mix at their usual build; a hero is moved off it by how far the souls in their cells divide differently
/// from how they usually do (<see cref="HeroTypicalBuild"/>):
/// <code>
/// d(shop)   = share of the souls seen in the hero's cells − the hero's usual share
/// lean      = d(weapon) − d(spirit)
/// ramp      = clamp(souls spent ÷ FullRampSouls, 0, 1)
/// bullet   += Alpha × ramp × lean          spirit −= Alpha × ramp × lean   (kept inside the trait's scale)
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
    /// Points of damage share (the traits' scale is a percentage) a hero's mix moves by when every soul they spent went one shop's way
    /// instead of the other's from where they usually are. Measured from finished matches with <c>BuildShiftFit</c> (docs/scoring_model.md,
    /// "What a hero has built"): 24.4 for the bullet share and 27.3 for the ability share.
    /// </summary>
    public const double Alpha = 26;

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
        var costs = MedianCosts(store);
        var shifts = new List<HeroShift>();
        foreach (var heroId in match.Allies.Concat(match.Enemies).Concat(match.SelfHero is { } self ? [self] : []).Distinct())
        {
            if (match.BuildOf(heroId) is { } build && store.TypicalBuilds.TryGetValue(heroId, out var usual)
                && Shift(store, heroId, build, usual, costs) is { } shift)
                shifts.Add(shift);
        }
        return Of(shifts);
    }

    /// <summary>One hero's shift, or null if their build moves them nowhere.</summary>
    /// <param name="costs">What a cell of each shop whose tier wasn't read counts for (<see cref="MedianCosts"/>).</param>
    public static HeroShift? Shift(
        DataStore store, string heroId, HeroBuild build, IReadOnlyDictionary<ShopCategory, double> usual,
        IReadOnlyDictionary<ShopCategory, int> costs)
    {
        if (!store.IsProfiled(heroId))
            return null;
        var souls = Enum.GetValues<ShopCategory>().ToDictionary(category => category, _ => 0.0);
        foreach (var cell in build.Cells.Where(cell => cell.Category is not null))
            souls[cell.Category!.Value] += cell.Cost ?? costs.GetValueOrDefault(cell.Category.Value);
        var spent = souls.Values.Sum();
        if (spent <= 0)
            return null;

        var built = souls.ToDictionary(pair => pair.Key, pair => pair.Value / spent);
        var lean = (built[ShopCategory.Weapon] - usual.GetValueOrDefault(ShopCategory.Weapon))
                   - (built[ShopCategory.Spirit] - usual.GetValueOrDefault(ShopCategory.Spirit));
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
        return traits.Count == 0 ? null : new HeroShift(heroId, traits, lean, ramp, (int)spent, built, usual);
    }

    /// <summary>The median tier cost of the shop's items, for a cell whose item and tier weren't read.</summary>
    public static Dictionary<ShopCategory, int> MedianCosts(DataStore store)
    {
        var costs = new Dictionary<ShopCategory, int>();
        foreach (var group in store.Items.Values
                     .Where(item => item.Tier is >= 1 and <= 4 && ItemTemplates.CategoryOf(item.Category) is not null)
                     .GroupBy(item => ItemTemplates.CategoryOf(item.Category)!.Value))
        {
            var sorted = group.Select(item => HeroBuild.TierCost(item.Tier)).Order().ToList();
            costs[group.Key] = sorted[sorted.Count / 2];
        }
        return costs;
    }
}
