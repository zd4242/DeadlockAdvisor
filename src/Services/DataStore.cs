using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Formats;

namespace DeadlockAdvisor.Services;

public sealed record Coverage(
    int HeroesTotal,
    int HeroesProfiled,
    int ItemsTotal,
    int ItemsTagged,
    int Rules,
    int DerivedRules,
    int ScoresFilled,
    int ScoresTotal);

/// <param name="ItemCount">How many current items carry the stat.</param>
public sealed record StatCatalogEntry(string Label, string Unit, int ItemCount);

/// <summary>
/// The CSV tables under data/, held in memory and written back when they're edited. No UI here,
/// so the data layer and the scoring maths can be tested on their own.
/// <para>
/// An item's coefficient for one (category, relation) has three sources:
/// <c>effective = trait_weight × (manual + from_stats)</c>. <c>manual</c> is the hand-typed
/// coefficient; <c>from_stats</c> comes from the item's real numbers through stat_rules.csv
/// (a conditional stat counts at the rule's conditional_factor); the trait weight scales a whole
/// (category, relation) at once.
/// </para>
/// <para>
/// Every table keeps insertion order, as the Python dicts it mirrors do: several outputs, and the
/// order rows are saved in, depend on it.
/// </para>
/// </summary>
public sealed class DataStore
{
    /// <summary>What a new stat rule's suggested per_unit aims a typical item at: a mild coefficient.</summary>
    public const double StatRuleTarget = 2.0;

    public const string HeroesFile = "heroes.csv";
    public const string ItemsFile = "items.csv";
    public const string CategoriesFile = "categories.csv";
    public const string HeroScoresFile = "hero_category_scores.csv";
    public const string ItemCoefficientsFile = "item_formula_coefficients.csv";
    public const string TraitWeightsFile = "trait_weights.csv";
    public const string StatRulesFile = "stat_rules.csv";
    public const string ItemStatsFile = "item_stats.csv";
    public const string ItemTooltipsFile = "item_tooltips.json";
    public const string MatchLiftFile = "match_item_lift.csv";
    public const string MatchMetaFile = "match_item_lift.meta.json";
    public const string MatchCountsFile = "match_item_counts.json";

    private const int _missingOrder = 9999;

    public string DataDir { get; set; }

    public OrderedDictionary<string, Hero> Heroes { get; set; } = [];
    public OrderedDictionary<string, Item> Items { get; set; } = [];
    public OrderedDictionary<string, Category> Categories { get; set; } = [];
    public OrderedDictionary<ScoreKey, double> HeroScores { get; set; } = [];

    /// <summary>Keyed so the same triple can't appear twice and double-count.</summary>
    public OrderedDictionary<CoefficientKey, double> ItemCoefficients { get; set; } = [];

    /// <summary>
    /// Item × trait × relation lines scored on their best targets (<see cref="BestTargets"/>) on top of what
    /// <see cref="Item.CastOn"/> already covers: an item that pays off once per cooldown, however many heroes
    /// have the trait. Covers the line's typed and stat-derived parts alike. Never "as": that's one hero.
    /// </summary>
    public HashSet<CoefficientKey> BestTargetLines { get; set; } = [];

    /// <summary>Absent means 1.</summary>
    public OrderedDictionary<WeightKey, double> TraitWeights { get; set; } = [];

    public OrderedDictionary<StatRuleKey, StatRule> StatRules { get; set; } = [];

    /// <summary>Each item's stats, as the game sync wrote them.</summary>
    public OrderedDictionary<string, List<ItemStat>> ItemStats { get; set; } = [];

    /// <summary>Each item's in-game tooltip, as the game sync wrote it.</summary>
    public OrderedDictionary<string, ItemTooltip> ItemTooltips { get; set; } = [];

    /// <summary>Lifts from real matches. Empty until Fetch Match Stats has run; everything works without it.</summary>
    public OrderedDictionary<MatchLiftKey, MatchLift> MatchLift { get; set; } = [];

    /// <summary>The sidecar: fetched_at, the rank range, per-family windows and reliability.</summary>
    public JsonObject MatchMeta
    {
        get => _matchMeta;
        set
        {
            _matchMeta = value;
            _buildRatios = null;
        }
    }
    private JsonObject _matchMeta = [];

    /// <summary>
    /// What Fetch Match Stats downloaded, split by rank, that <see cref="MatchLift"/> was worked out from.
    /// Null when the lifts predate rank splits (or there are none), so they can't be refiltered.
    /// </summary>
    public MatchCounts? MatchCounts
    {
        get => _matchCounts;
        set
        {
            _matchCounts = value;
            _buildRatios = null;
        }
    }
    private MatchCounts? _matchCounts;

    /// <summary>
    /// How often each hero builds each item next to the average player (<see cref="MatchStatsMath.BuildRatios"/>),
    /// over the lifts' rank range; empty without <see cref="MatchCounts"/>. Worked out when first asked for.
    /// </summary>
    public IReadOnlyDictionary<(string ItemId, string HeroId), double> BuildRatios =>
        _buildRatios ??= MatchCounts is null ? [] : MatchStatsMath.BuildRatios(MatchCounts, MatchStatsMath.RankOf(MatchMeta), Items.Values);
    private Dictionary<(string ItemId, string HeroId), double>? _buildRatios;

    /// <summary>The stat parts making up each stat-derived coefficient. Computed by <see cref="RebuildDerived"/>, never saved.</summary>
    public OrderedDictionary<CoefficientKey, List<StatPart>> Derived { get; set; } = [];

    public DataStore(string dataDir)
    {
        DataDir = dataDir;
    }

    public static DataStore Load(string dataDir)
    {
        var store = new DataStore(dataDir);
        store.LoadHeroes();
        store.LoadItems();
        store.LoadCategories();
        store.LoadHeroScores();
        store.LoadItemCoefficients();
        store.LoadTraitWeights();
        store.LoadStatRules();
        store.LoadItemStats();
        store.LoadItemTooltips();
        store.LoadMatchLift();
        store.LoadMatchCounts();
        store.RebuildDerived();
        return store;
    }

    // -- loaders ----------------------------------------------------------------

    private string PathOf(string fileName) => Path.Combine(DataDir, fileName);

    private List<CsvRow>? ReadOptional(string fileName)
    {
        var path = PathOf(fileName);
        return File.Exists(path) ? CsvReader.ReadFile(path) : null;
    }

    internal void LoadHeroes()
    {
        foreach (var row in CsvReader.ReadFile(PathOf(HeroesFile)))
        {
            if (!row.Has("hero_id"))
                continue;
            var id = row.Required("hero_id");
            Heroes[id] = new Hero(id, row.Required("hero_name"), NumberFormat.Truncate(NumberFormat.ToFloat(row.Get("game_id"))));
        }
    }

    internal void LoadItems()
    {
        foreach (var row in CsvReader.ReadFile(PathOf(ItemsFile)))
        {
            if (!row.Has("item_id"))
                continue;
            var id = row.Required("item_id");
            Items[id] = new Item(
                id,
                row.Required("item_name"),
                row.Required("category"),
                NumberFormat.ParseInt(row.Required("tier")),
                NumberFormat.Truncate(NumberFormat.ToFloat(row.Get("game_id"))),
                (int)NumberFormat.Truncate(NumberFormat.ToFloat(row.Get("cost"))),
                CastOn(row.Get("single_target")));
        }
    }

    /// <summary>"against" or "with"; a 1 from before the column named a side is an enemy, until the next sync says.</summary>
    private static Relation? CastOn(string? text) =>
        Relations.TryParse(text?.Trim(), out var relation) && relation != Relation.As ? relation
        : IsTrue(text) ? Relation.Against
        : null;

    internal void LoadCategories()
    {
        foreach (var row in CsvReader.ReadFile(PathOf(CategoriesFile)))
        {
            if (!row.Has("category_id"))
                continue;
            var id = row.Required("category_id");
            Categories[id] = new Category(
                id,
                row.Required("category_name"),
                NumberFormat.ParseFloat(row.Required("scale_min")),
                NumberFormat.ParseFloat(row.Required("scale_max")),
                row.Required("description"));
        }
    }

    internal void LoadHeroScores()
    {
        foreach (var row in ReadOptional(HeroScoresFile) ?? [])
        {
            if (!row.Has("hero_id") || !row.Has("category_id"))
                continue;
            HeroScores[new ScoreKey(row.Required("hero_id"), row.Required("category_id"))] = NumberFormat.ToFloat(row.Get("score"));
        }
    }

    internal void LoadItemCoefficients()
    {
        foreach (var row in ReadOptional(ItemCoefficientsFile) ?? [])
        {
            if (!row.Has("item_id") || !row.Has("category_id"))
                continue;
            if (!Relations.TryParse(row.Get("relation")?.Trim(), out var relation))
                continue;
            var key = new CoefficientKey(row.Required("item_id"), row.Required("category_id"), relation);
            // A row with no typed number can still mark a stat-derived line as best-target.
            if (relation != Relation.As && IsTrue(row.Get("best_target")))
                BestTargetLines.Add(key);
            var value = NumberFormat.ToFloat(row.Get("coefficient"));
            if (value != 0)
                ItemCoefficients[key] = value;
        }
    }

    internal void LoadTraitWeights()
    {
        foreach (var row in ReadOptional(TraitWeightsFile) ?? [])
        {
            if (!row.Has("category_id") || !Relations.TryParse(row.Get("relation")?.Trim(), out var relation))
                continue;
            var weight = NumberFormat.ToFloat(row.Get("weight"), 1.0);
            if (weight != 1.0)
                TraitWeights[new WeightKey(row.Required("category_id"), relation)] = weight;
        }
    }

    internal void LoadStatRules()
    {
        foreach (var row in ReadOptional(StatRulesFile) ?? [])
        {
            var stat = (row.Get("stat") ?? "").Trim();
            var categoryId = (row.Get("category_id") ?? "").Trim();
            if (stat.Length == 0 || categoryId.Length == 0 || !Relations.TryParse(row.Get("relation")?.Trim(), out var relation))
                continue;
            // A blank conditional_factor means "no discount": an invisible default of anything
            // else would be a surprise.
            StatRules[new StatRuleKey(stat, categoryId, relation)] = new StatRule(
                stat, categoryId, relation,
                NumberFormat.ToFloat(row.Get("per_unit")),
                NumberFormat.ToFloat(row.Get("conditional_factor"), 1.0),
                (row.Get("note") ?? "").Trim());
        }
    }

    internal void LoadItemStats()
    {
        foreach (var row in ReadOptional(ItemStatsFile) ?? [])
        {
            if (!row.Has("item_id") || !row.Has("stat"))
                continue;
            var itemId = row.Required("item_id");
            var stat = row.Required("stat");
            if (!ItemStats.TryGetValue(itemId, out var stats))
            {
                stats = [];
                ItemStats[itemId] = stats;
            }
            stats.Add(new ItemStat(
                itemId, stat, row.Or("label", stat),
                NumberFormat.ToFloat(row.Get("value")),
                row.Or("unit", ""),
                IsTrue(row.Get("conditional"))));
        }
    }

    /// <summary>A CSV flag: "1", "true" or "yes", in any case; anything else, or a missing column, is false.</summary>
    private static bool IsTrue(string? text) => (text ?? "").Trim().ToLowerInvariant() is "1" or "true" or "yes";

    internal void LoadItemTooltips()
    {
        var path = PathOf(ItemTooltipsFile);
        if (!File.Exists(path))
            return;
        if (PythonJson.Parse(File.ReadAllText(path, Encoding.UTF8)) is not JsonObject tooltips)
            return;
        foreach (var (itemId, data) in tooltips)
        {
            if (data is JsonObject tooltip)
                ItemTooltips[itemId] = ItemTooltip.FromJson(itemId, tooltip);
        }
    }

    /// <summary>(Re)read the match data. Public so a finished fetch can pick it up without reloading everything else.</summary>
    public void LoadMatchLift()
    {
        MatchLift = [];
        MatchMeta = [];
        var rows = ReadOptional(MatchLiftFile);
        if (rows is null)
            return;

        foreach (var row in rows)
        {
            // Downloads from before lane data was dropped also hold lane-phase rows.
            if (!row.Has("item_id") || !row.Has("hero_id") || row.Or("scope", "full") != "full")
                continue;
            var lift = new MatchLift(
                row.Required("item_id"), row.Required("hero_id"), row.Or("relation", ""),
                (int)NumberFormat.Truncate(NumberFormat.ToFloat(row.Get("matches"))),
                NumberFormat.ToFloat(row.Get("lift")),
                NumberFormat.ToFloat(row.Get("se")),
                NumberFormat.ToFloat(row.Get("lift_shrunk")));
            MatchLift[new MatchLiftKey(lift.ItemId, lift.HeroId, lift.Relation)] = lift;
        }

        var meta = PathOf(MatchMetaFile);
        if (File.Exists(meta) && PythonJson.Parse(File.ReadAllText(meta, Encoding.UTF8)) is JsonObject metaObject)
            MatchMeta = metaObject;
    }

    /// <summary>A counts file that can't be read is left out like a missing one: the lifts work without it, and the next fetch rewrites it.</summary>
    internal void LoadMatchCounts()
    {
        MatchCounts = null;
        var path = PathOf(MatchCountsFile);
        if (!File.Exists(path))
            return;
        try
        {
            MatchCounts = MatchCounts.Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException
                                       or FormatException or IndexOutOfRangeException)
        {
        }
    }

    /// <summary>
    /// Recompute every stat-derived coefficient from the item stats and stat rules. Cheap: call it after
    /// either changes. The game sync calls it after changing items too, so the build ratios start over.
    /// </summary>
    public void RebuildDerived()
    {
        _buildRatios = null;
        var rulesByStat = new Dictionary<string, List<StatRule>>();
        foreach (var rule in StatRules.Values)
        {
            if (!Categories.ContainsKey(rule.CategoryId) || rule.PerUnit == 0)
                continue;
            if (!rulesByStat.TryGetValue(rule.Stat, out var rules))
            {
                rules = [];
                rulesByStat[rule.Stat] = rules;
            }
            rules.Add(rule);
        }

        var derived = new OrderedDictionary<CoefficientKey, List<StatPart>>();
        foreach (var (itemId, stats) in ItemStats)
        {
            if (!Items.ContainsKey(itemId))
                continue;
            foreach (var stat in stats)
            {
                if (!rulesByStat.TryGetValue(stat.Stat, out var rules))
                    continue;
                foreach (var rule in rules)
                {
                    var factor = stat.Conditional ? rule.ConditionalFactor : 1.0;
                    if (factor == 0)
                        continue;
                    var key = new CoefficientKey(itemId, rule.CategoryId, rule.Relation);
                    if (!derived.TryGetValue(key, out var parts))
                    {
                        parts = [];
                        derived[key] = parts;
                    }
                    parts.Add(new StatPart(stat.Stat, stat.Label, stat.Unit, stat.Value, rule.PerUnit, factor, stat.Conditional));
                }
            }
        }
        Derived = derived;
    }

    // -- reads ------------------------------------------------------------------

    public double HeroScore(string heroId, string categoryId) =>
        HeroScores.GetValueOrDefault(new ScoreKey(heroId, categoryId));

    public bool IsProfiled(string heroId) => Categories.Keys.Any(categoryId => HeroScore(heroId, categoryId) != 0);

    /// <summary>
    /// Each trait's average over the profiled heroes. Scoring counts a hero's trait relative to it, so a
    /// trait the whole roster shares doesn't hand its items the same bonus in every match. Unprofiled
    /// heroes are left out: all zeros would read as "below average at everything".
    /// </summary>
    public Dictionary<string, double> TraitBaselines()
    {
        var profiled = Heroes.Keys.Where(IsProfiled).ToList();
        var baselines = new Dictionary<string, double>();
        foreach (var categoryId in Categories.Keys)
        {
            var total = 0.0;
            foreach (var heroId in profiled)
                total += HeroScore(heroId, categoryId);
            baselines[categoryId] = profiled.Count == 0 ? 0.0 : total / profiled.Count;
        }
        return baselines;
    }

    /// <summary>The hand-typed coefficient only; see <see cref="EffectiveCoefficient"/>.</summary>
    public double Coefficient(string itemId, string categoryId, Relation relation) =>
        ItemCoefficients.GetValueOrDefault(new CoefficientKey(itemId, categoryId, relation));

    public double TraitWeight(string categoryId, Relation relation) =>
        TraitWeights.TryGetValue(new WeightKey(categoryId, relation), out var weight) ? weight : 1.0;

    public IReadOnlyList<StatPart> DerivedParts(string itemId, string categoryId, Relation relation) =>
        Derived.TryGetValue(new CoefficientKey(itemId, categoryId, relation), out var parts) ? parts : [];

    public double DerivedCoefficient(string itemId, string categoryId, Relation relation) =>
        SumAmounts(DerivedParts(itemId, categoryId, relation));

    /// <summary>What scoring actually multiplies a hero's trait score by.</summary>
    public double EffectiveCoefficient(string itemId, string categoryId, Relation relation)
    {
        var baseValue = Coefficient(itemId, categoryId, relation) + DerivedCoefficient(itemId, categoryId, relation);
        return TraitWeight(categoryId, relation) * baseValue;
    }

    /// <summary>Every nonzero effective coefficient, manual or stat-derived.</summary>
    public OrderedDictionary<CoefficientKey, double> EffectiveCoefficients()
    {
        var result = new OrderedDictionary<CoefficientKey, double>();
        foreach (var key in ItemCoefficients.Keys.Concat(Derived.Keys))
        {
            if (result.ContainsKey(key))
                continue;
            var value = EffectiveCoefficient(key.ItemId, key.CategoryId, key.Relation);
            if (value != 0)
                result[key] = value;
        }
        return result;
    }

    /// <summary>The game sync found the item cast on one hero, which puts every line on this relation on its best targets.</summary>
    public bool CastOnCovers(string itemId, Relation relation) =>
        Items.TryGetValue(itemId, out var item) && item.CastOn is { } castOn && BestTargets.AppliesTo(castOn, relation);

    /// <summary>Whether this line counts its best targets (<see cref="BestTargets"/>) rather than summing over the team.</summary>
    public bool OnBestTargets(string itemId, string categoryId, Relation relation) =>
        CastOnCovers(itemId, relation) || BestTargetLines.Contains(new CoefficientKey(itemId, categoryId, relation));

    /// <summary>Any line of the item that scores something on this relation counts its best targets.</summary>
    public bool HasBestTargetLines(string itemId, Relation relation) =>
        Categories.Keys.Any(categoryId =>
            OnBestTargets(itemId, categoryId, relation) && EffectiveCoefficient(itemId, categoryId, relation) != 0);

    /// <summary>
    /// Every (category, relation, coefficient) rule on one item, ordered the way the categories are,
    /// so the list is stable as you edit.
    /// </summary>
    public List<(string CategoryId, Relation Relation, double Coefficient)> RulesForItem(string itemId)
    {
        var order = CategoryOrder();
        return ItemCoefficients
            .Where(entry => entry.Key.ItemId == itemId)
            .Select(entry => (entry.Key.CategoryId, entry.Key.Relation, entry.Value))
            .OrderBy(rule => order.GetValueOrDefault(rule.CategoryId, 999))
            .ThenBy(rule => rule.Relation)
            .ToList();
    }

    public int RuleCount(string itemId) => ItemCoefficients.Keys.Count(key => key.ItemId == itemId);

    /// <summary>Every (category, relation, stat parts) an item picks up from its stats, in <see cref="RulesForItem"/>'s order.</summary>
    public List<(string CategoryId, Relation Relation, IReadOnlyList<StatPart> Parts)> DerivedRulesForItem(string itemId)
    {
        var order = CategoryOrder();
        return Derived
            .Where(entry => entry.Key.ItemId == itemId)
            .Select(entry => (entry.Key.CategoryId, entry.Key.Relation, (IReadOnlyList<StatPart>)entry.Value))
            .OrderBy(rule => order.GetValueOrDefault(rule.CategoryId, 999))
            .ThenBy(rule => rule.Relation)
            .ToList();
    }

    /// <summary>Items built from this one: the inverse of a tooltip's components.</summary>
    public List<string> UpgradesTo(string itemId) =>
        ItemTooltips
            .Where(entry => entry.Value.Components.Contains(itemId) && Items.ContainsKey(entry.Key))
            .Select(entry => entry.Key)
            .ToList();

    /// <summary>
    /// Whether a lowercased search string fuzzily matches the item's name, or appears as typed in its id or anywhere
    /// on its tooltip card, where a fuzzy match would find almost anything.
    /// </summary>
    public bool ItemMatches(string itemId, string needle)
    {
        if (Items.TryGetValue(itemId, out var item) && FuzzyMatch.Score(needle, item.ItemName) is not null)
            return true;
        if (itemId.ToLowerInvariant().Contains(needle, StringComparison.Ordinal))
            return true;
        return ItemTooltips.TryGetValue(itemId, out var tooltip) && tooltip.SearchText.Contains(needle, StringComparison.Ordinal);
    }

    public int DerivedRuleCount(string itemId) => Derived.Keys.Count(key => key.ItemId == itemId);

    /// <summary>The inverse view: every item that responds to one trait, for bulk-tagging a whole category in one pass.</summary>
    public OrderedDictionary<string, double> ItemsForCategory(string categoryId, Relation relation)
    {
        var result = new OrderedDictionary<string, double>();
        foreach (var (key, value) in ItemCoefficients)
        {
            if (key.CategoryId == categoryId && key.Relation == relation)
                result[key.ItemId] = value;
        }
        return result;
    }

    public OrderedDictionary<string, double> DerivedItemsForCategory(string categoryId, Relation relation)
    {
        var result = new OrderedDictionary<string, double>();
        foreach (var (key, parts) in Derived)
        {
            if (key.CategoryId == categoryId && key.Relation == relation)
                result[key.ItemId] = SumAmounts(parts);
        }
        return result;
    }

    public List<Hero> HeroesSorted() =>
        Heroes.Values.OrderBy(hero => hero.HeroName, StringComparer.Ordinal).ToList();

    public List<Item> ItemsSorted() =>
        Items.Values.OrderBy(item => item.Tier).ThenBy(item => item.ItemName, StringComparer.Ordinal).ToList();

    public List<Category> CategoriesOrdered() => Categories.Values.ToList();

    // -- writes -----------------------------------------------------------------

    /// <summary>True if the value actually changed, so callers can skip a pointless save and rescore.</summary>
    public bool SetHeroScore(string heroId, string categoryId, double score)
    {
        if (HeroScore(heroId, categoryId) == score)
            return false;
        HeroScores[new ScoreKey(heroId, categoryId)] = score;
        return true;
    }

    /// <summary>Set a formula rule. 0 removes the rule entirely: the table is sparse, and a zero row would only be noise.</summary>
    public bool SetCoefficient(string itemId, string categoryId, Relation relation, double value)
    {
        var key = new CoefficientKey(itemId, categoryId, relation);
        if (ItemCoefficients.GetValueOrDefault(key) == value)
            return false;
        if (value == 0)
            ItemCoefficients.Remove(key);
        else
            ItemCoefficients[key] = value;
        return true;
    }

    /// <summary>Mark or unmark one line as scored on its best targets; refused on "as", which is only ever one hero.</summary>
    public bool SetBestTarget(string itemId, string categoryId, Relation relation, bool onBestTargets)
    {
        if (relation == Relation.As)
            return false;
        var key = new CoefficientKey(itemId, categoryId, relation);
        return onBestTargets ? BestTargetLines.Add(key) : BestTargetLines.Remove(key);
    }

    /// <summary>Scale a whole (trait, relation) at once. 1 removes the row: the table is sparse, like the coefficients.</summary>
    public bool SetTraitWeight(string categoryId, Relation relation, double weight)
    {
        var key = new WeightKey(categoryId, relation);
        if (TraitWeight(categoryId, relation) == weight)
            return false;
        if (weight == 1.0)
            TraitWeights.Remove(key);
        else
            TraitWeights[key] = weight;
        return true;
    }

    // -- stat rules -------------------------------------------------------------

    /// <summary>Every stat at least one current item carries, with its label, unit and item count, sorted by label.</summary>
    public OrderedDictionary<string, StatCatalogEntry> StatCatalog()
    {
        var found = new OrderedDictionary<string, (string Label, string Unit, HashSet<string> Items)>();
        foreach (var (itemId, stats) in ItemStats)
        {
            if (!Items.ContainsKey(itemId))
                continue;
            foreach (var stat in stats)
            {
                if (!found.TryGetValue(stat.Stat, out var entry))
                {
                    entry = (stat.Label, stat.Unit, []);
                    found[stat.Stat] = entry;
                }
                entry.Items.Add(itemId);
            }
        }

        var result = new OrderedDictionary<string, StatCatalogEntry>();
        foreach (var (stat, entry) in found.OrderBy(pair => pair.Value.Label, StringComparer.Ordinal))
            result[stat] = new StatCatalogEntry(entry.Label, entry.Unit, entry.Items.Count);
        return result;
    }

    public List<StatRule> StatRulesFor(string categoryId, Relation relation) =>
        StatRules.Values.Where(rule => rule.CategoryId == categoryId && rule.Relation == relation).ToList();

    /// <summary>
    /// Add or update a stat rule, or retarget one to a different stat (<paramref name="replacing"/> is
    /// the stat it used to read). Keeps its place in the file, and rebuilds the stat-derived coefficients.
    /// </summary>
    public bool SetStatRule(StatRule rule, string? replacing = null)
    {
        var key = new StatRuleKey(rule.Stat, rule.CategoryId, rule.Relation);
        var oldKey = string.IsNullOrEmpty(replacing) ? key : new StatRuleKey(replacing, rule.CategoryId, rule.Relation);
        if (StatRules.TryGetValue(oldKey, out var current) && current == rule)
            return false;
        // That stat already has a rule on this trait.
        if (oldKey != key && StatRules.ContainsKey(key))
            return false;

        var position = StatRules.IndexOf(oldKey);
        if (position < 0)
            position = StatRules.Count;
        else
            StatRules.RemoveAt(position);
        StatRules.Insert(Math.Min(position, StatRules.Count), key, rule);
        RebuildDerived();
        return true;
    }

    public bool RemoveStatRule(string stat, string categoryId, Relation relation)
    {
        if (!StatRules.Remove(new StatRuleKey(stat, categoryId, relation)))
            return false;
        RebuildDerived();
        return true;
    }

    /// <summary>
    /// A starting per_unit that puts a typical item carrying this stat at <paramref name="target"/>, a
    /// mild coefficient, rounded to two significant figures.
    /// </summary>
    public double SuggestPerUnit(string stat, double target = StatRuleTarget)
    {
        // Sorted by size but keeping the sign, so a stat items usually carry as a negative number
        // still gets a rate that scores them positively.
        var values = ItemStats
            .Where(entry => Items.ContainsKey(entry.Key))
            .SelectMany(entry => entry.Value)
            .Where(s => s.Stat == stat && s.Value != 0 && !s.Conditional)
            .Select(s => s.Value)
            .OrderBy(Math.Abs)
            .ToList();
        if (values.Count == 0)
        {
            values = ItemStats.Values
                .SelectMany(stats => stats)
                .Where(s => s.Stat == stat && s.Value != 0)
                .Select(s => s.Value)
                .OrderBy(Math.Abs)
                .ToList();
        }
        if (values.Count == 0)
            return 0.1;
        return NumberFormat.ParseFloat(NumberFormat.G(target / values[values.Count / 2], 2));
    }

    public bool ClearHeroScores(string heroId)
    {
        var changed = false;
        foreach (var categoryId in Categories.Keys)
        {
            var key = new ScoreKey(heroId, categoryId);
            if (HeroScores.GetValueOrDefault(key) != 0.0)
            {
                HeroScores[key] = 0.0;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Clone one hero's whole trait profile onto another: the fast way to start a hero who plays like one you've rated.</summary>
    public bool CopyHeroScores(string sourceHeroId, string targetHeroId)
    {
        var changed = false;
        foreach (var categoryId in Categories.Keys)
        {
            if (SetHeroScore(targetHeroId, categoryId, HeroScore(sourceHeroId, categoryId)))
                changed = true;
        }
        return changed;
    }

    public bool ClearItemRules(string itemId)
    {
        var keys = ItemCoefficients.Keys.Where(key => key.ItemId == itemId).ToList();
        foreach (var key in keys)
            ItemCoefficients.Remove(key);
        var flags = BestTargetLines.RemoveWhere(key => key.ItemId == itemId);
        return keys.Count > 0 || flags > 0;
    }

    public bool CopyItemRules(string sourceItemId, string targetItemId)
    {
        var changed = ClearItemRules(targetItemId);
        foreach (var (categoryId, relation, value) in RulesForItem(sourceItemId))
        {
            if (SetCoefficient(targetItemId, categoryId, relation, value))
                changed = true;
        }
        foreach (var key in BestTargetLines.Where(key => key.ItemId == sourceItemId).ToList())
        {
            if (SetBestTarget(targetItemId, key.CategoryId, key.Relation, true))
                changed = true;
        }
        return changed;
    }

    // -- persistence ------------------------------------------------------------

    public void SaveHeroScores()
    {
        var rows = Heroes.Keys.SelectMany(heroId => Categories.Keys.Select(categoryId =>
            Row(heroId, categoryId, NumberFormat.Python(HeroScore(heroId, categoryId)))));
        WriteCsv(HeroScoresFile, ["hero_id", "category_id", "score"], rows);
    }

    public void SaveItemCoefficients()
    {
        var itemOrder = IndexOf(Items.Keys);
        var categoryOrder = CategoryOrder();
        var rows = ItemCoefficients.Keys
            .Concat(BestTargetLines.Where(key => !ItemCoefficients.ContainsKey(key)))
            .OrderBy(key => itemOrder.GetValueOrDefault(key.ItemId, _missingOrder))
            .ThenBy(key => categoryOrder.GetValueOrDefault(key.CategoryId, _missingOrder))
            .ThenBy(key => key.Relation)
            .Select(key => Row(key.ItemId, key.CategoryId, key.Relation.Key(), NumberFormat.Python(ItemCoefficients.GetValueOrDefault(key)),
                BestTargetLines.Contains(key) ? "1" : ""));
        WriteCsv(ItemCoefficientsFile, ["item_id", "category_id", "relation", "coefficient", "best_target"], rows);
    }

    public void SaveTraitWeights()
    {
        var categoryOrder = CategoryOrder();
        var rows = TraitWeights
            .OrderBy(entry => categoryOrder.GetValueOrDefault(entry.Key.CategoryId, _missingOrder))
            .ThenBy(entry => entry.Key.Relation)
            .Select(entry => Row(entry.Key.CategoryId, entry.Key.Relation.Key(), NumberFormat.Python(entry.Value)));
        WriteCsv(TraitWeightsFile, ["category_id", "relation", "weight"], rows);
    }

    public void SaveStatRules()
    {
        var rows = StatRules.Values.Select(rule => Row(
            rule.Stat, rule.CategoryId, rule.Relation.Key(), NumberFormat.Python(rule.PerUnit),
            NumberFormat.Python(rule.ConditionalFactor), rule.Note));
        WriteCsv(StatRulesFile, ["stat", "category_id", "relation", "per_unit", "conditional_factor", "note"], rows);
    }

    public void SaveHeroes()
    {
        var rows = Heroes.Values.Select(hero => Row(hero.HeroId, hero.HeroName, Integer(hero.GameId)));
        WriteCsv(HeroesFile, ["hero_id", "hero_name", "game_id"], rows);
    }

    public void SaveItems()
    {
        var rows = Items.Values.Select(item => Row(
            item.ItemId, item.ItemName, item.Category, Integer(item.Tier), Integer(item.GameId), Integer(item.Cost), item.CastOn?.Key() ?? ""));
        WriteCsv(ItemsFile, ["item_id", "item_name", "category", "tier", "game_id", "cost", "single_target"], rows);
    }

    /// <summary>Written by the game sync, never edited by hand: the next sync would overwrite the edit anyway.</summary>
    public void SaveItemStats()
    {
        var itemOrder = IndexOf(Items.Keys);
        var rows = ItemStats.Keys
            .OrderBy(itemId => itemOrder.GetValueOrDefault(itemId, _missingOrder))
            .ThenBy(itemId => itemId, StringComparer.Ordinal)
            .SelectMany(itemId => ItemStats[itemId])
            .Select(stat => Row(
                stat.ItemId, stat.Stat, stat.Label, NumberFormat.Python(stat.Value), stat.Unit, stat.Conditional ? "1" : "0"));
        WriteCsv(ItemStatsFile, ["item_id", "stat", "label", "value", "unit", "conditional"], rows);
    }

    /// <summary>
    /// Generated by the game sync like item_stats.csv, but nested, hence JSON. No backup: the next
    /// sync rebuilds it from the API anyway.
    /// </summary>
    public void SaveItemTooltips()
    {
        var itemOrder = IndexOf(Items.Keys);
        var data = new JsonObject();
        foreach (var itemId in ItemTooltips.Keys
                     .OrderBy(itemId => itemOrder.GetValueOrDefault(itemId, _missingOrder))
                     .ThenBy(itemId => itemId, StringComparer.Ordinal))
        {
            data[itemId] = ItemTooltips[itemId].ToJson();
        }
        AtomicFile.Write(PathOf(ItemTooltipsFile), PythonJson.ToFileBytes(data, ensureAscii: false));
    }

    /// <summary>
    /// Generated by Fetch Match Stats, never edited by hand. The CSV goes first: a meta file
    /// describing rows that aren't there would be worse than the other way round.
    /// </summary>
    public void SaveMatchLift()
    {
        var itemOrder = IndexOf(Items.Keys);
        var rows = MatchLift.Values
            .OrderBy(lift => lift.Relation, StringComparer.Ordinal)
            .ThenBy(lift => lift.HeroId, StringComparer.Ordinal)
            .ThenBy(lift => itemOrder.GetValueOrDefault(lift.ItemId, _missingOrder))
            .Select(lift => Row(
                lift.ItemId, lift.HeroId, lift.Relation, Integer(lift.Matches),
                NumberFormat.Fixed(lift.Lift, 3), NumberFormat.Fixed(lift.Se, 3), NumberFormat.Fixed(lift.LiftShrunk, 3)));
        WriteCsv(MatchLiftFile, ["item_id", "hero_id", "relation", "matches", "lift", "se", "lift_shrunk"], rows);
        AtomicFile.Write(PathOf(MatchMetaFile), PythonJson.ToFileBytes(MatchMeta, ensureAscii: true));
    }

    /// <summary>Generated by Fetch Match Stats, and big: no backup, the next fetch rebuilds it anyway.</summary>
    public void SaveMatchCounts()
    {
        if (MatchCounts is not null)
            AtomicFile.Write(PathOf(MatchCountsFile), MatchCounts.ToJsonBytes());
    }

    public void SaveAll()
    {
        SaveHeroScores();
        SaveItemCoefficients();
        SaveTraitWeights();
    }

    private void WriteCsv(string fileName, IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows) =>
        BackedUpFile.Write(PathOf(fileName), CsvWriter.ToBytes(header, rows));

    private static IReadOnlyList<string> Row(params string[] fields) => fields;

    private static string Integer(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // -- growth / maintenance ---------------------------------------------------

    /// <summary>
    /// Backfill any missing (hero, category) row with a score of 0, without touching scores already
    /// entered. Call after adding a hero or a category, then save. Returns how many rows were added.
    /// </summary>
    public int SyncCategories()
    {
        var added = 0;
        foreach (var heroId in Heroes.Keys)
        {
            foreach (var categoryId in Categories.Keys)
            {
                var key = new ScoreKey(heroId, categoryId);
                if (HeroScores.ContainsKey(key))
                    continue;
                HeroScores[key] = 0.0;
                added++;
            }
        }
        return added;
    }

    /// <summary>Drop score, coefficient and weight rows pointing at ids that no longer exist.</summary>
    public int PruneOrphans()
    {
        var removed = 0;
        foreach (var key in HeroScores.Keys.Where(k => !Heroes.ContainsKey(k.HeroId) || !Categories.ContainsKey(k.CategoryId)).ToList())
        {
            HeroScores.Remove(key);
            removed++;
        }
        foreach (var key in ItemCoefficients.Keys.Where(k => !Items.ContainsKey(k.ItemId) || !Categories.ContainsKey(k.CategoryId)).ToList())
        {
            ItemCoefficients.Remove(key);
            removed++;
        }
        removed += BestTargetLines.RemoveWhere(key => !Items.ContainsKey(key.ItemId) || !Categories.ContainsKey(key.CategoryId));
        foreach (var key in TraitWeights.Keys.Where(k => !Categories.ContainsKey(k.CategoryId)).ToList())
        {
            TraitWeights.Remove(key);
            removed++;
        }
        RebuildDerived();
        return removed;
    }

    /// <summary>Items with no rules at all, hand-typed or from stats: these always score 0 until something tags them.</summary>
    public List<string> UncoveredItems()
    {
        var covered = ItemCoefficients.Keys.Select(key => key.ItemId)
            .Concat(Derived.Keys.Select(key => key.ItemId))
            .ToHashSet();
        return Items.Keys.Where(itemId => !covered.Contains(itemId)).ToList();
    }

    /// <summary>Heroes whose trait scores are still all zero: they contribute nothing to any recommendation yet.</summary>
    public List<string> UnprofiledHeroes() => Heroes.Keys.Where(heroId => !IsProfiled(heroId)).ToList();

    public int HeroFilledCount(string heroId) =>
        Categories.Keys.Count(categoryId => HeroScore(heroId, categoryId) != 0);

    /// <summary>Headline numbers for the status bar.</summary>
    public Coverage Coverage() => new(
        HeroesTotal: Heroes.Count,
        HeroesProfiled: Heroes.Count - UnprofiledHeroes().Count,
        ItemsTotal: Items.Count,
        ItemsTagged: Items.Count - UncoveredItems().Count,
        Rules: ItemCoefficients.Count,
        DerivedRules: Derived.Count,
        ScoresFilled: HeroScores.Values.Count(value => value != 0),
        ScoresTotal: Heroes.Count * Categories.Count);

    // -- helpers ----------------------------------------------------------------

    private Dictionary<string, int> CategoryOrder() => IndexOf(Categories.Keys);

    private static Dictionary<string, int> IndexOf(IEnumerable<string> ids)
    {
        var index = new Dictionary<string, int>();
        foreach (var id in ids)
            index.TryAdd(id, index.Count);
        return index;
    }

    /// <summary>Left-to-right, as Python's sum() adds, so totals match to the last bit.</summary>
    internal static double SumAmounts(IEnumerable<StatPart> parts)
    {
        var total = 0.0;
        foreach (var part in parts)
            total += part.Amount;
        return total;
    }
}
