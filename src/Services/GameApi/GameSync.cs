using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services.Formats;

namespace DeadlockAdvisor.Services.GameApi;

/// <summary>
/// Folding what the community Deadlock API (deadlock-api.com) knows into the store: game ids for
/// heroes and items (what any stats query joins on), the shop list itself (new items are added;
/// tier, shop and cost follow the game), each item's stat numbers for stat_rules.csv, and each
/// item's tooltip for the hover card. No network here: <see cref="GameApiService"/> fetches, and
/// <see cref="Apply"/> only changes the store in memory; <see cref="SaveSynced"/> writes what changed.
/// </summary>
public static partial class GameSync
{
    public const string Api = "https://api.deadlock-api.com/v1/assets";

    /// <summary>Tier 5 "legendary" items aren't in the main game mode, so recommending them would only be noise.</summary>
    public static readonly IReadOnlySet<long> ShopTiers = new HashSet<long> { 1, 2, 3, 4 };

    /// <summary>
    /// API property → (stat, label, unit). The stat is the name stat_rules.csv uses. Several properties
    /// can feed one stat (the API files Spirit Power under three names; a conditional top-up is its
    /// own property), so one rule catches every item. To offer a new stat to rules, add it here and re-sync.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Stat, string Label, string Unit)> Stats =
        new Dictionary<string, (string, string, string)>
        {
            ["TechResist"] = ("TechResist", "Spirit Resist", "%"),
            ["TechResistBelowThreshold"] = ("TechResist", "Spirit Resist", "%"),
            ["BulletResist"] = ("BulletResist", "Bullet Resist", "%"),
            ["BulletResistBelowThreshold"] = ("BulletResist", "Bullet Resist", "%"),
            ["MeleeResistPercent"] = ("MeleeResistPercent", "Melee Resist", "%"),
            ["BonusHealth"] = ("BonusHealth", "Bonus Health", ""),
            ["BonusBaseHealth"] = ("BonusBaseHealth", "Base Health", "%"),
            ["TechPower"] = ("TechPower", "Spirit Power", ""),
            ["SpiritPower"] = ("TechPower", "Spirit Power", ""),
            ["SpiritPowerInnate"] = ("TechPower", "Spirit Power", ""),
            ["TechPowerPercent"] = ("TechPowerPercent", "Spirit Power", "%"),
            ["BaseAttackDamagePercent"] = ("BaseAttackDamagePercent", "Weapon Damage", "%"),
            ["BonusFireRate"] = ("BonusFireRate", "Fire Rate", "%"),
            ["BonusClipSizePercent"] = ("BonusClipSizePercent", "Max Ammo", "%"),
            ["BonusMeleeDamagePercent"] = ("BonusMeleeDamagePercent", "Melee Damage", "%"),
            ["BulletLifestealPercent"] = ("BulletLifestealPercent", "Bullet Lifesteal", "%"),
            ["AbilityLifestealPercentHero"] = ("AbilityLifestealPercentHero", "Spirit Lifesteal", "%"),
            ["AbilityLifestealPercentHeroPassive"] = ("AbilityLifestealPercentHero", "Spirit Lifesteal", "%"),
            ["BonusHealthRegen"] = ("BonusHealthRegen", "Health Regen", ""),
            ["OutOfCombatHealthRegen"] = ("OutOfCombatHealthRegen", "Out of Combat Regen", ""),
            ["HealAmpCastPercent"] = ("HealAmpCastPercent", "Healing Effectiveness", "%"),
            ["HealAmpReceivePenaltyPercent"] = ("HealAmpReceivePenaltyPercent", "Healing Reduction", "%"),
            ["StatusResistancePercent"] = ("StatusResistancePercent", "Debuff Resist", "%"),
            ["InnateStatusResistancePercent"] = ("StatusResistancePercent", "Debuff Resist", "%"),
            ["SlowResistancePercent"] = ("SlowResistancePercent", "Slow Resist", "%"),
            ["BonusMoveSpeed"] = ("BonusMoveSpeed", "Move Speed", "m/s"),
            ["BonusSprintSpeed"] = ("BonusSprintSpeed", "Sprint Speed", "m/s"),
            ["CooldownReduction"] = ("CooldownReduction", "Cooldown Reduction", "%"),
            ["BonusAbilityDurationPercent"] = ("BonusAbilityDurationPercent", "Ability Duration", "%"),
            ["TechRangeMultiplier"] = ("TechRangeMultiplier", "Ability Range", "%"),
        };

    /// <summary>Stored negated relative to the game's display: Healbane's "35% Healing Reduction" arrives as -35.</summary>
    private static readonly HashSet<string> _negated = ["HealAmpReceivePenaltyPercent"];

    /// <summary>Stats the flags get wrong: this fire rate "charges up over time" after casting, but is filed as a plain passive.</summary>
    private static readonly HashSet<(string?, string)> _forceConditional =
    [
        ("Mercurial Magnum", "BonusFireRate"),
        ("Quicksilver Reload", "BonusFireRate"),
    ];

    /// <summary>Real effects the tooltip layout doesn't list, confirmed in game.</summary>
    private static readonly HashSet<(string?, string)> _forceShown = [("Crippling Headshot", "HealAmpReceivePenaltyPercent")];

    /// <summary>Penalties on the item's owner, filed like the ones put on enemies: Cheat Death's healing reduction is the price of its immunity.</summary>
    private static readonly HashSet<(string?, string)> _selfInflicted = [("Cheat Death", "HealAmpReceivePenaltyPercent")];

    private static readonly string[] _shownKeys = ["properties", "important_properties", "elevated_properties"];

    // -- names ----------------------------------------------------------------------

    /// <summary>Name → match key: "Mo &amp; Krill" and "The Doorman" have to land on our hero_id spellings.</summary>
    public static string Norm(string name)
    {
        var key = NonAlphanumeric().Replace(name.ToLowerInvariant().Replace("&", "and"), "");
        return key.StartsWith("the", StringComparison.Ordinal) && key.Length > 3 ? key[3..] : key;
    }

    /// <summary>"Diviner's Kevlar" → "diviners_kevlar", matching the existing ids.</summary>
    public static string MakeId(string name)
    {
        var key = name.ToLowerInvariant().Replace("&", "and").Replace("'", "");
        return NonAlphanumeric().Replace(key, "_").Trim('_');
    }

    /// <summary>"30" → 30, "0.75m" → 0.75; null for anything that isn't a number.</summary>
    public static double? Number(JsonNode? value)
    {
        var match = LeadingNumber().Match(value is null ? "" : PyJson.Str(value));
        return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    // -- stats ----------------------------------------------------------------------

    /// <summary>
    /// Whether a stat needs something to happen before it applies. The API's ConditionallyApplied
    /// flag is the main signal; beyond that, a stat on an active needs the active pressed, a passive
    /// whose other stats are flagged is one conditional effect, and a passive "provided in ability"
    /// is a triggered buff. Innate stats and plain passives are always on.
    /// </summary>
    private static bool IsConditional(JsonNode property, bool passiveHasCondition)
    {
        var section = PyJson.Get(property, "tooltip_section");
        var sectionName = section is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (PyJson.Contains(property, "usage_flags", "ConditionallyApplied") || sectionName == "active")
            return true;
        if (sectionName == "passive")
            return passiveHasCondition || PyJson.Contains(property, "usage_flags", "IntrinsicallyProvidedInAbility");
        return false;
    }

    /// <summary>Every property the item's in-game tooltip shows, or null with no tooltip layout to check against.</summary>
    private static HashSet<string>? DisplayedProperties(JsonNode record)
    {
        var sections = PyJson.Get(record, "tooltip_sections");
        if (!PyJson.Truthy(sections))
            return null;
        var shown = new HashSet<string>(StringComparer.Ordinal);
        Walk(sections);
        return shown;

        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj)
                    {
                        if (_shownKeys.Contains(key) && value is JsonArray list)
                        {
                            foreach (var entry in list)
                            {
                                if (entry is JsonValue v && v.TryGetValue<string>(out var name))
                                    shown.Add(name);
                            }
                        }
                        Walk(value);
                    }
                    break;
                case JsonArray array:
                    foreach (var value in array)
                        Walk(value);
                    break;
            }
        }
    }

    /// <summary>
    /// One stat per (stat, conditional) the item has, summing the properties folded into it. Only
    /// stats the tooltip shows count: the game files carry leftovers the game never applies
    /// (Refresher still has resists on file from before its rework).
    /// </summary>
    public static List<ItemStat> ExtractStats(string itemId, JsonNode record)
    {
        var shown = DisplayedProperties(record);
        var name = PyJson.Get(record, "name") is JsonValue nameValue && nameValue.TryGetValue<string>(out var n) ? n : null;
        var properties = (PyJson.Get(record, "properties") as JsonObject ?? [])
            .Where(pair => pair.Value is JsonObject && (shown is null || shown.Contains(pair.Key) || _forceShown.Contains((name, pair.Key))))
            .Select(pair => (Key: pair.Key, Property: pair.Value!))
            .ToList();
        var passiveHasCondition = properties.Any(p =>
            PyJson.Get(p.Property, "tooltip_section") is JsonValue section && section.TryGetValue<string>(out var s) && s == "passive"
            && PyJson.Contains(p.Property, "usage_flags", "ConditionallyApplied"));

        var totals = new OrderedDictionary<(string Stat, bool Conditional), double>();
        var meta = new Dictionary<string, (string Label, string Unit)>();
        foreach (var (key, property) in properties)
        {
            if (!Stats.TryGetValue(key, out var stat) || _selfInflicted.Contains((name, key)))
                continue;
            if (Number(PyJson.Get(property, "value")) is not { } value || value == 0)
                continue;
            if (_negated.Contains(key))
                value = -value;
            var conditional = IsConditional(property, passiveHasCondition) || _forceConditional.Contains((name, key));
            var slot = (stat.Stat, conditional);
            totals[slot] = totals.GetValueOrDefault(slot) + value;
            meta[stat.Stat] = (stat.Label, stat.Unit);
        }

        return totals
            .OrderBy(pair => pair.Key.Stat, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Conditional)
            .Where(pair => pair.Value != 0)
            .Select(pair => new ItemStat(itemId, pair.Key.Stat, meta[pair.Key.Stat].Label, NumberFormat.Round(pair.Value, 4),
                meta[pair.Key.Stat].Unit, pair.Key.Conditional))
            .ToList();
    }

    // -- tooltips ---------------------------------------------------------------------

    /// <summary>A property as the tooltip prints it, or null for one with nothing to show (no label, or a zero: unused cooldowns come as 0).</summary>
    private static TooltipStat? PrintedStat(JsonNode? property)
    {
        if (property is not JsonObject || !PyJson.Truthy(PyJson.Get(property, "label")))
            return null;
        if (Number(PyJson.Get(property, "value")) is not { } number || number == 0)
            return null;
        var prefix = PyJson.Text(property, "prefix");
        if (prefix == "{s:sign}")
            prefix = number > 0 ? "+" : "-";
        var value = prefix is "+" or "-" ? prefix + Format.Num(Math.Abs(number)) : Format.Num(number);
        return new TooltipStat(
            value + PyJson.Text(property, "postfix"),
            PyJson.Str(PyJson.Get(property, "label")),
            PyJson.Contains(property, "usage_flags", "ConditionallyApplied"),
            PyJson.Truthy(PyJson.Get(property, "negative_attribute")));
    }

    /// <summary>
    /// The item's tooltip sections with every property reference resolved to its printed value.
    /// <paramref name="classToItem"/> maps the API's class names to our item ids, for the components.
    /// </summary>
    public static ItemTooltip? ExtractTooltip(string itemId, JsonNode record, IReadOnlyDictionary<string, string> classToItem)
    {
        var properties = PyJson.Get(record, "properties");

        EquatableList<TooltipStat> StatsOf(IEnumerable<string> keys, IReadOnlyDictionary<string, string>? effects = null) =>
            keys.Select(key => effects is not null && effects.TryGetValue(key, out var effect)
                    ? new TooltipStat("", effect)
                    : PrintedStat(PyJson.Get(properties, key)))
                .OfType<TooltipStat>()
                .ToEquatableList();

        var sections = new List<TooltipSection>();
        foreach (var raw in PyJson.Items(record, "tooltip_sections"))
        {
            // A handful of passives come without a section_type.
            var kind = PyJson.Text(raw, "section_type");
            if (kind.Length == 0)
                kind = PyJson.Truthy(PyJson.Get(record, "is_active_item")) ? "active" : "passive";
            var cooldown = "";
            var blocks = new List<TooltipBlock>();
            foreach (var attribute in PyJson.Items(raw, "section_attributes"))
            {
                var keys = PyJson.Strings(attribute, "properties").ToList();
                // The cooldown goes in the section's header strip, as in game.
                if (keys.Remove("AbilityCooldown") && PrintedStat(PyJson.Get(properties, "AbilityCooldown")) is { } cooldownStat)
                    cooldown = cooldownStat.Value;

                // Status effects (Silenced, Stun...) are boxes with a name and no number.
                var effects = new Dictionary<string, string>();
                foreach (var effect in PyJson.Items(attribute, "important_properties_with_icon"))
                {
                    if (effect is JsonObject && PyJson.Truthy(PyJson.Get(effect, "localized_name")))
                        effects[PyJson.Str(PyJson.Get(effect, "name"))] = PyJson.Str(PyJson.Get(effect, "localized_name"));
                }

                var block = new TooltipBlock(
                    TooltipText.From(PyJson.Text(attribute, "loc_string")),
                    StatsOf(PyJson.Strings(attribute, "elevated_properties")),
                    StatsOf(PyJson.Strings(attribute, "important_properties"), effects),
                    StatsOf(keys));
                if (block != TooltipBlock.Empty)
                    blocks.Add(block);
            }
            if (blocks.Count > 0 || cooldown.Length > 0)
                sections.Add(new TooltipSection(kind, cooldown, blocks.ToEquatableList()));
        }

        var components = PyJson.Strings(record, "component_items")
            .Where(classToItem.ContainsKey)
            .Select(name => classToItem[name])
            .ToEquatableList();
        if (sections.Count == 0 && components.Count == 0)
            return null;
        return new ItemTooltip(itemId, sections.ToEquatableList(), components);
    }

    // -- applying to the store ----------------------------------------------------------

    /// <summary>Fold API records into the store in memory. Existing ids and names are kept; game ids, tiers, shop categories and costs follow the game.</summary>
    public static SyncReport Apply(DataStore store, IEnumerable<JsonNode?> heroRecords, IEnumerable<JsonNode?> itemRecords)
    {
        var report = new SyncReport();
        ApplyHeroes(store, heroRecords.OfType<JsonNode>().ToList(), report);
        var recordsByItem = ApplyItems(store, itemRecords.OfType<JsonNode>().ToList(), report);
        ApplyStats(store, recordsByItem, report);
        ApplyTooltips(store, recordsByItem, report);
        if (report.AddedHeroes.Count > 0)
            store.SyncCategories();
        return report;
    }

    private static void ApplyHeroes(DataStore store, List<JsonNode> records, SyncReport report)
    {
        var ours = new Dictionary<string, Hero>();
        foreach (var hero in store.Heroes.Values)
            ours[Norm(hero.HeroName)] = hero;
        foreach (var hero in store.Heroes.Values)
            ours[Norm(hero.HeroId)] = hero;

        foreach (var record in records)
        {
            var name = PyJson.Text(record, "name");
            var gameId = PyJson.Int(record, "id");
            if (!ours.TryGetValue(Norm(name), out var hero))
            {
                var heroId = MakeId(name);
                if (heroId.Length == 0 || store.Heroes.ContainsKey(heroId))
                    continue;
                store.Heroes[heroId] = new Hero(heroId, name, gameId);
                report.AddedHeroes.Add(name);
                report.HeroesChanged = true;
            }
            else if (hero.GameId != gameId)
            {
                store.Heroes[hero.HeroId] = hero with { GameId = gameId };
                report.HeroesChanged = true;
                if (hero.GameId != 0)
                    report.Changed.Add($"{hero.HeroName}: game id {hero.GameId} -> {gameId}");
                else
                    report.Filled++;
            }
        }
    }

    /// <returns>Item id → API record, for every item the game still sells.</returns>
    private static OrderedDictionary<string, JsonNode> ApplyItems(DataStore store, List<JsonNode> records, SyncReport report)
    {
        var ours = new Dictionary<string, Item>();
        foreach (var item in store.Items.Values)
            ours[Norm(item.ItemName)] = item;
        var matched = new OrderedDictionary<string, JsonNode>();

        foreach (var record in records)
        {
            var name = PyJson.Text(record, "name");
            var gameId = PyJson.Int(record, "id");
            var tier = (int)PyJson.Int(record, "item_tier");
            var category = PyJson.Text(record, "item_slot_type");
            var cost = (int)PyJson.Int(record, "cost");

            if (!ours.TryGetValue(Norm(name), out var current))
            {
                var itemId = MakeId(name);
                if (itemId.Length == 0 || store.Items.ContainsKey(itemId))
                    continue;
                store.Items[itemId] = new Item(itemId, name, category, tier, gameId, cost);
                matched[itemId] = record;
                report.AddedItems.Add($"{name} (T{tier})");
                report.ItemsChanged = true;
                continue;
            }

            matched[current.ItemId] = record;
            var updated = new Item(current.ItemId, current.ItemName, category, tier, gameId, cost);
            if (updated == current)
                continue;
            // A missing game id or cost is just the first sync filling columns in; a tier or shop
            // move is a patch, and worth saying out loud.
            var before = report.Changed.Count;
            if (current.Tier != tier)
                report.Changed.Add($"{current.ItemName}: tier {current.Tier} -> {tier}");
            if (current.Category != category)
                report.Changed.Add($"{current.ItemName}: shop {current.Category} -> {category}");
            if (current.Cost != 0 && current.Cost != cost)
                report.Changed.Add($"{current.ItemName}: cost {current.Cost} -> {cost}");
            if (current.GameId != 0 && current.GameId != gameId)
                report.Changed.Add($"{current.ItemName}: game id {current.GameId} -> {gameId}");
            if (report.Changed.Count == before)
                report.Filled++;
            store.Items[current.ItemId] = updated;
            report.ItemsChanged = true;
        }

        report.NotInGame.AddRange(store.Items.Keys.Where(itemId => !matched.ContainsKey(itemId)).Select(itemId => store.Items[itemId].ItemName));
        return matched;
    }

    private static void ApplyStats(DataStore store, OrderedDictionary<string, JsonNode> recordsByItem, SyncReport report)
    {
        var fresh = new OrderedDictionary<string, List<ItemStat>>();
        foreach (var (itemId, record) in recordsByItem)
        {
            var stats = ExtractStats(itemId, record);
            if (stats.Count > 0)
                fresh[itemId] = stats;
        }
        report.StatRows = fresh.Values.Sum(stats => stats.Count);
        var same = fresh.Count == store.ItemStats.Count
                   && fresh.All(pair => store.ItemStats.TryGetValue(pair.Key, out var old) && old.SequenceEqual(pair.Value));
        if (same)
            return;
        store.ItemStats = fresh;
        store.RebuildDerived();
        report.StatsChanged = true;
    }

    private static void ApplyTooltips(DataStore store, OrderedDictionary<string, JsonNode> recordsByItem, SyncReport report)
    {
        var classToItem = new Dictionary<string, string>();
        foreach (var (itemId, record) in recordsByItem)
            classToItem[PyJson.Str(PyJson.Get(record, "class_name"))] = itemId;

        var fresh = new OrderedDictionary<string, ItemTooltip>();
        foreach (var (itemId, record) in recordsByItem)
        {
            if (ExtractTooltip(itemId, record, classToItem) is { } tooltip)
                fresh[itemId] = tooltip;
        }
        report.TooltipCount = fresh.Count;
        var same = fresh.Count == store.ItemTooltips.Count
                   && fresh.All(pair => store.ItemTooltips.TryGetValue(pair.Key, out var old) && old == pair.Value);
        if (same)
            return;
        store.ItemTooltips = fresh;
        report.TooltipsChanged = true;
    }

    /// <summary>Write only the files the sync actually changed, so a no-op sync doesn't churn backups.</summary>
    public static void SaveSynced(DataStore store, SyncReport report)
    {
        if (report.HeroesChanged)
            store.SaveHeroes();
        if (report.AddedHeroes.Count > 0)
            store.SaveHeroScores();
        if (report.ItemsChanged)
            store.SaveItems();
        if (report.StatsChanged)
            store.SaveItemStats();
        if (report.TooltipsChanged)
            store.SaveItemTooltips();
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex(@"^" + PyText.Space + @"*(-?[0-9]+(?:\.[0-9]+)?)")]
    private static partial Regex LeadingNumber();
}
