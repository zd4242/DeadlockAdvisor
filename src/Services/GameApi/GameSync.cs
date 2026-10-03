using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
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
            ["BuffTechResist"] = ("TechResist", "Spirit Resist", "%"),
            ["BulletResist"] = ("BulletResist", "Bullet Resist", "%"),
            ["BulletResistBelowThreshold"] = ("BulletResist", "Bullet Resist", "%"),
            ["BuffBulletResist"] = ("BulletResist", "Bullet Resist", "%"),
            // Escalating Resilience's fully stacked resist, labelled "Max Bullet Resist".
            ["MaxArmorStacks"] = ("BulletResist", "Bullet Resist", "%"),
            ["MeleeResistPercent"] = ("MeleeResistPercent", "Melee Resist", "%"),
            ["BonusHealth"] = ("BonusHealth", "Bonus Health", ""),
            ["BonusBaseHealth"] = ("BonusBaseHealth", "Base Health", "%"),
            // Barriers on yourself only: Guardian Ward's goes on an ally.
            ["CombatBarrier"] = ("Barrier", "Barrier", ""),
            ["VexBarrierCombatBarrier"] = ("Barrier", "Barrier", ""),
            ["TechPower"] = ("TechPower", "Spirit Power", ""),
            ["SpiritPower"] = ("TechPower", "Spirit Power", ""),
            ["SpiritPowerInnate"] = ("TechPower", "Spirit Power", ""),
            ["AmbushBonusTechPower"] = ("TechPower", "Spirit Power", ""),
            ["BonusSpirit"] = ("TechPower", "Spirit Power", ""),
            ["TechPowerPercent"] = ("TechPowerPercent", "Spirit Power", "%"),
            ["BaseAttackDamagePercent"] = ("BaseAttackDamagePercent", "Weapon Damage", "%"),
            ["BaseAttackDamagePercentBonus"] = ("BaseAttackDamagePercent", "Weapon Damage", "%"),
            // Intensifying Magazine's bonus after firing continuously, labelled "Max Weapon Damage".
            ["BaseAttackDamagePercentAtMaxDuration"] = ("BaseAttackDamagePercent", "Weapon Damage", "%"),
            ["CloseRangeBonusWeaponPower"] = ("BaseAttackDamagePercent", "Weapon Damage", "%"),
            ["LongRangeBonusWeaponPower"] = ("BaseAttackDamagePercent", "Weapon Damage", "%"),
            ["WeaponPowerPerStack"] = ("BaseAttackDamagePercent", "Weapon Damage", "%"),
            ["WeaponDamagePerStack"] = ("BaseAttackDamagePercent", "Weapon Damage", "%"),
            ["BonusFireRate"] = ("BonusFireRate", "Fire Rate", "%"),
            ["ActiveBonusFireRate"] = ("BonusFireRate", "Fire Rate", "%"),
            ["ActivatedFireRate"] = ("BonusFireRate", "Fire Rate", "%"),
            ["AmbushBonusFireRate"] = ("BonusFireRate", "Fire Rate", "%"),
            ["FervorFireRate"] = ("BonusFireRate", "Fire Rate", "%"),
            ["FireRateBonus"] = ("BonusFireRate", "Fire Rate", "%"),
            ["BonusClipSizePercent"] = ("BonusClipSizePercent", "Max Ammo", "%"),
            ["BonusMeleeDamagePercent"] = ("BonusMeleeDamagePercent", "Melee Damage", "%"),
            ["AmbushBonusMeleeDamage"] = ("BonusMeleeDamagePercent", "Melee Damage", "%"),
            ["BulletLifestealPercent"] = ("BulletLifestealPercent", "Bullet Lifesteal", "%"),
            ["ActiveBonusLifesteal"] = ("BulletLifestealPercent", "Bullet Lifesteal", "%"),
            ["AbilityLifestealPercentHero"] = ("AbilityLifestealPercentHero", "Spirit Lifesteal", "%"),
            ["AbilityLifestealPercentHeroPassive"] = ("AbilityLifestealPercentHero", "Spirit Lifesteal", "%"),
            ["BonusSpiritLifesteal"] = ("AbilityLifestealPercentHero", "Spirit Lifesteal", "%"),
            ["BonusHealthRegen"] = ("BonusHealthRegen", "Health Regen", ""),
            ["OutOfCombatHealthRegen"] = ("OutOfCombatHealthRegen", "Out of Combat Regen", ""),
            ["HealAmpCastPercent"] = ("HealAmpCastPercent", "Healing Effectiveness", "%"),
            ["HealAmpReceivePenaltyPercent"] = ("HealAmpReceivePenaltyPercent", "Healing Reduction", "%"),
            ["StatusResistancePercent"] = ("StatusResistancePercent", "Debuff Resist", "%"),
            ["InnateStatusResistancePercent"] = ("StatusResistancePercent", "Debuff Resist", "%"),
            ["FervorStatusResistancePercent"] = ("StatusResistancePercent", "Debuff Resist", "%"),
            ["SlowResistancePercent"] = ("SlowResistancePercent", "Slow Resist", "%"),
            ["BonusMoveSpeed"] = ("BonusMoveSpeed", "Move Speed", "m/s"),
            ["ActiveBonusMoveSpeed"] = ("BonusMoveSpeed", "Move Speed", "m/s"),
            ["FervorMovespeed"] = ("BonusMoveSpeed", "Move Speed", "m/s"),
            ["BonusSprintSpeed"] = ("BonusSprintSpeed", "Sprint Speed", "m/s"),
            ["StackingBonusSprintSpeed"] = ("BonusSprintSpeed", "Sprint Speed", "m/s"),
            ["CooldownReduction"] = ("CooldownReduction", "Cooldown Reduction", "%"),
            ["BonusAbilityCharges"] = ("AbilityCharges", "Bonus Ability Charges", ""),
            ["CooldownReductionOnChargedAbilities"] = ("ChargeCooldownPercent", "Cooldown Reduction For Charged Abilities", "%"),
            ["CooldownBetweenChargeReduction"] = ("ChargeRechargePercent", "Faster Time Between Charges", "%"),
            ["BonusSpiritForChargedAbilities"] = ("ChargedTechPower", "Spirit Power for Charged Abilities", ""),
            ["BulletDamageReflectedPct"] = ("BulletDamageReturned", "Bullet Damage Returned", "%"),
            ["SpiritDamageReflectedPct"] = ("SpiritDamageReturned", "Spirit Damage Returned", "%"),
            ["BonusAbilityDurationPercent"] = ("BonusAbilityDurationPercent", "Ability Duration", "%"),
            ["TechRangeMultiplier"] = ("TechRangeMultiplier", "Ability Range", "%"),
            ["TechRangeMultiplierBuff"] = ("TechRangeMultiplier", "Ability Range", "%"),
            ["StackingTechRangeMultiplier"] = ("TechRangeMultiplier", "Ability Range", "%"),
        };

    /// <summary>
    /// Properties the tooltip shows under a scored stat's label that are left out on purpose, and why.
    /// Any other property sharing such a label is reported by the sync (<see cref="UnmappedStats"/>),
    /// so a patch's new or renamed property gets sorted into <see cref="Stats"/> or here instead of
    /// being silently dropped.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Unscored = new Dictionary<string, string>
    {
        ["BulletArmorReduction"] = "a debuff on enemies",
        ["BulletResistReduction"] = "a debuff on enemies",
        ["MagicResistReduction"] = "a debuff on enemies",
        ["TechArmorDamageReduction"] = "a debuff on enemies",
        ["TechPowerReduction"] = "a debuff on enemies",
        ["FireRateSlow"] = "a debuff on enemies",
        ["SlowPercent"] = "a debuff on enemies",
        ["MovementSpeedSlow"] = "a debuff on enemies",
        ["MaxSlowPercent"] = "a debuff on enemies",
        ["HealLifePercentOutOfCombat"] = "a percent of max health per second, not the flat amount Out of Combat Regen counts",
        ["GuardianWardCombatBarrier"] = "goes on an ally",
        ["HealAmpRegenPenaltyPercent"] = "the regen half of a healing reduction HealAmpReceivePenaltyPercent already counts",
        ["ProcBaseAttackDamagePercent"] = "one proc shot, not a lasting bonus",
    };

    /// <summary>
    /// Properties whose value is per stack of the item's buff. They're counted fully stacked (see
    /// <see cref="StackCount"/>) and as conditional, since the stacks have to be built up first.
    /// </summary>
    private static readonly HashSet<string> _perStack =
        ["WeaponPowerPerStack", "WeaponDamagePerStack", "StackingBonusSprintSpeed", "StackingTechRangeMultiplier"];

    /// <summary>Per-stack values the API files under a plain name: Spellslinger's Fire Rate is per stack of its buff.</summary>
    private static readonly HashSet<(string?, string)> _forcePerStack = [("Spellslinger", "BonusFireRate")];

    /// <summary>Stack counts for items the game doesn't cap: Ballistic Enchantment stacks once per unique hero the ability hits.</summary>
    private static readonly Dictionary<string, int> _assumedStacks = new() { ["Ballistic Enchantment"] = 2 };

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

    /// <summary>
    /// Actives with a cast range and no radius that still don't pick one hero: Silence Wave's projectile
    /// hits everyone in its path, and Warp Stone's range is how far you teleport.
    /// </summary>
    private static readonly HashSet<string> _notSingleTarget = ["Silence Wave", "Warp Stone"];

    /// <summary>
    /// Items only as good as their best target that have no targeted active to flag them: Counterspell's
    /// parry blocks one enemy ability per cooldown, so you save it for the one that matters most.
    /// </summary>
    private static readonly Dictionary<string, Relation> _forceSingleTarget = new() { ["Counterspell"] = Relation.Against };

    /// <summary>
    /// Shop → the stat its investment bonus raises. Every soul spent in a shop adds to that bonus, so
    /// each of its items carries a share of it: cost × the bonus per soul (<see cref="ShopBonuses"/>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Stat, string Label, string Unit)> ShopBonusStats =
        new Dictionary<string, (string, string, string)>
        {
            ["weapon"] = ("ShopWeaponDamage", "Weapon Damage (shop bonus)", "%"),
            ["spirit"] = ("ShopSpiritPower", "Spirit Power (shop bonus)", ""),
            ["vitality"] = ("ShopBaseHealth", "Base Health (shop bonus)", "%"),
        };

    private static readonly HashSet<string> _shopBonusStatNames = ShopBonusStats.Values.Select(stat => stat.Stat).ToHashSet();

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
        var match = LeadingNumber().Match(value is null ? "" : JsonRecord.Str(value));
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
        var section = JsonRecord.Get(property, "tooltip_section");
        var sectionName = section is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (JsonRecord.Contains(property, "usage_flags", "ConditionallyApplied") || sectionName == "active")
            return true;
        if (sectionName == "passive")
            return passiveHasCondition || JsonRecord.Contains(property, "usage_flags", "IntrinsicallyProvidedInAbility");
        return false;
    }

    /// <summary>Every property the item's in-game tooltip shows, or null with no tooltip layout to check against.</summary>
    private static HashSet<string>? DisplayedProperties(JsonNode record)
    {
        var sections = JsonRecord.Get(record, "tooltip_sections");
        if (!JsonRecord.Truthy(sections))
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
    /// (Refresher still has resists on file from before its rework). With <paramref name="shopBonuses"/>,
    /// the item also carries its share of its shop's investment bonus.
    /// </summary>
    public static List<ItemStat> ExtractStats(string itemId, JsonNode record, IReadOnlyDictionary<string, ShopBonus>? shopBonuses = null)
    {
        var name = NameOf(record);
        var properties = ShownProperties(record);
        var passiveHasCondition = PassiveHasCondition(properties);
        var stacks = StackCount(record) ?? 1;

        var totals = new OrderedDictionary<(string Stat, bool Conditional), double>();
        var meta = new Dictionary<string, (string Label, string Unit)>();
        foreach (var (key, property) in properties)
        {
            if (!Stats.TryGetValue(key, out var stat) || _selfInflicted.Contains((name, key)))
                continue;
            if (Number(JsonRecord.Get(property, "value")) is not { } value || value == 0)
                continue;
            if (_negated.Contains(key))
                value = -value;
            var perStack = IsPerStack(name, key);
            if (perStack)
                value *= stacks;
            var conditional = perStack || IsConditional(property, passiveHasCondition) || _forceConditional.Contains((name, key));
            var slot = (stat.Stat, conditional);
            totals[slot] = totals.GetValueOrDefault(slot) + value;
            meta[stat.Stat] = (stat.Label, stat.Unit);
        }

        var shop = JsonRecord.Text(record, "item_slot_type");
        if (shopBonuses?.GetValueOrDefault(shop) is { } bonus && ShopBonusStats.TryGetValue(shop, out var shopStat))
        {
            totals[(shopStat.Stat, false)] = bonus.Share(JsonRecord.Int(record, "cost"));
            meta[shopStat.Stat] = (shopStat.Label, shopStat.Unit);
        }

        return totals
            .OrderBy(pair => pair.Key.Stat, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Conditional)
            .Where(pair => pair.Value != 0)
            .Select(pair => new ItemStat(itemId, pair.Key.Stat, meta[pair.Key.Stat].Label, NumberFormat.Round(pair.Value, 4),
                meta[pair.Key.Stat].Unit, pair.Key.Conditional))
            .ToList();
    }

    /// <summary>
    /// Each shop's investment bonus, from the heroes' <c>cost_bonuses</c> curves. Every hero has the
    /// same curves, so the first hero with any speaks for all; empty when none has them.
    /// </summary>
    public static Dictionary<string, ShopBonus> ShopBonuses(IEnumerable<JsonNode> heroRecords)
    {
        foreach (var record in heroRecords)
        {
            var bonuses = new Dictionary<string, ShopBonus>();
            foreach (var (shop, curve) in JsonRecord.Get(record, "cost_bonuses") as JsonObject ?? [])
            {
                var top = (curve as JsonArray ?? [])
                    .Select(step => new ShopBonus(
                        Number(JsonRecord.Get(step, "bonus")) ?? 0,
                        Number(JsonRecord.Get(step, "gold_threshold")) ?? 0))
                    .Where(step => step.Souls > 0)
                    .MaxBy(step => step.Souls);
                if (top is not null)
                    bonuses[shop] = top;
            }
            if (bonuses.Count > 0)
                return bonuses;
        }
        return [];
    }

    private static bool IsPerStack(string? name, string key) => _perStack.Contains(key) || _forcePerStack.Contains((name, key));

    /// <summary>How many stacks a per-stack property counts: the item's MaxStacks, else its <see cref="_assumedStacks"/> entry, else null.</summary>
    private static double? StackCount(JsonNode record)
    {
        var maxStacks = JsonRecord.Get(JsonRecord.Get(record, "properties"), "MaxStacks");
        if (Number(JsonRecord.Get(maxStacks, "value")) is { } max and > 0)
            return max;
        return NameOf(record) is { } name && _assumedStacks.TryGetValue(name, out var assumed) ? assumed : null;
    }

    private static string? NameOf(JsonNode record) =>
        JsonRecord.Get(record, "name") is JsonValue value && value.TryGetValue<string>(out var name) ? name : null;

    /// <summary>The item's properties its tooltip shows, plus the ones <see cref="_forceShown"/> adds; every property without a tooltip layout.</summary>
    private static List<(string Key, JsonNode Property)> ShownProperties(JsonNode record)
    {
        var shown = DisplayedProperties(record);
        var name = NameOf(record);
        return (JsonRecord.Get(record, "properties") as JsonObject ?? [])
            .Where(pair => pair.Value is JsonObject && (shown is null || shown.Contains(pair.Key) || _forceShown.Contains((name, pair.Key))))
            .Select(pair => (pair.Key, pair.Value!))
            .ToList();
    }

    /// <summary>
    /// The team the active is cast on one hero of, or null when it isn't single-target. It is when its
    /// tooltip shows a cast range and no radius (an area or an aura reaches more than one) and it isn't
    /// one of <see cref="_notSingleTarget"/>. Every ally-cast active's text says "Can be self-cast";
    /// no enemy-cast one does. <see cref="_forceSingleTarget"/> adds the ones no active shows.
    /// </summary>
    public static Relation? CastOn(JsonNode record) =>
        NameOf(record) is { } name && _forceSingleTarget.TryGetValue(name, out var forced) ? forced : ActiveCastOn(record);

    private static Relation? ActiveCastOn(JsonNode record)
    {
        if (!HasCastRangeAndNoRadius(record) || NameOf(record) is { } name && _notSingleTarget.Contains(name))
            return null;
        var selfCast = JsonRecord.Items(record, "tooltip_sections")
            .Where(section => JsonRecord.Text(section, "section_type") == "active")
            .SelectMany(section => JsonRecord.Items(section, "section_attributes"))
            .Any(attribute => JsonRecord.Text(attribute, "loc_string").Contains("self-cast", StringComparison.OrdinalIgnoreCase));
        return selfCast ? Relation.With : Relation.Against;
    }

    private static bool HasCastRangeAndNoRadius(JsonNode record)
    {
        if (!JsonRecord.Truthy(JsonRecord.Get(record, "is_active_item")))
            return false;
        var active = ShownProperties(record).Where(p => JsonRecord.Text(p.Property, "tooltip_section") == "active").Select(p => p.Key).ToList();
        return active.Contains("AbilityCastRange") && !active.Any(key => key.EndsWith("Radius", StringComparison.Ordinal));
    }

    private static bool PassiveHasCondition(IEnumerable<(string Key, JsonNode Property)> properties) =>
        properties.Any(p => JsonRecord.Text(p.Property, "tooltip_section") == "passive"
                            && JsonRecord.Contains(p.Property, "usage_flags", "ConditionallyApplied"));

    // -- drift: what a patch changed that the mapping above doesn't cover ------------------

    /// <summary>
    /// "Key ("Label"): Item, Item" for every shown, nonzero property that isn't in <see cref="Stats"/> or
    /// <see cref="Unscored"/> but carries a label a scored stat uses, or that label with "Max " in front
    /// (a fully stacked or charged-up value): most likely a new or renamed alias the stat rules are
    /// missing. Labels unrelated to any scored stat (Duration, Cast Range…) aren't reported.
    /// </summary>
    public static List<string> UnmappedStats(IReadOnlyCollection<JsonNode> records)
    {
        var shownByRecord = records.Select(record => (Name: NameOf(record) ?? "?", Properties: ShownProperties(record))).ToList();
        var scoredLabels = Stats.Values.Select(stat => stat.Label).ToHashSet(StringComparer.Ordinal);
        foreach (var (_, properties) in shownByRecord)
        {
            foreach (var (key, property) in properties)
            {
                if (Stats.ContainsKey(key) && JsonRecord.Text(property, "label") is { Length: > 0 } label)
                    scoredLabels.Add(label);
            }
        }

        var found = new OrderedDictionary<string, (string Label, List<string> Items)>();
        foreach (var (name, properties) in shownByRecord)
        {
            foreach (var (key, property) in properties)
            {
                if (Stats.ContainsKey(key) || Unscored.ContainsKey(key))
                    continue;
                var label = JsonRecord.Text(property, "label");
                var unprefixed = label.StartsWith("Max ", StringComparison.Ordinal) ? label["Max ".Length..] : label;
                if (!scoredLabels.Contains(label) && !scoredLabels.Contains(unprefixed) || Number(JsonRecord.Get(property, "value")) is not { } value || value == 0)
                    continue;
                if (!found.TryGetValue(key, out var entry))
                {
                    entry = (label, []);
                    found[key] = entry;
                }
                entry.Items.Add(name);
            }
        }
        return found
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key} (\"{pair.Value.Label}\"): {string.Join(", ", pair.Value.Items)}")
            .ToList();
    }

    /// <summary>
    /// Per-item overrides that no longer match the game, or that the game's own flags now make
    /// unnecessary; and per-stack stats on items with no stack count, which would count one stack.
    /// </summary>
    public static List<string> StaleOverrides(IReadOnlyCollection<JsonNode> records)
    {
        var byName = new Dictionary<string, JsonNode>();
        foreach (var record in records)
        {
            if (NameOf(record) is { } name)
                byName.TryAdd(name, record);
        }

        JsonNode? PropertyOf(string? name, string key) =>
            name is not null && byName.TryGetValue(name, out var record) && JsonRecord.Get(JsonRecord.Get(record, "properties"), key) is JsonObject property
                ? property
                : null;

        var stale = new List<string>();
        foreach (var (name, key) in _forceConditional)
        {
            if (PropertyOf(name, key) is not { } property)
                stale.Add($"{name} / {key}: forced conditional, but the item no longer has that property");
            else if (IsConditional(property, PassiveHasCondition(ShownProperties(byName[name!]))))
                stale.Add($"{name} / {key}: forced conditional, but the game now flags it conditional itself");
        }
        foreach (var (name, key) in _forceShown)
        {
            if (PropertyOf(name, key) is null)
                stale.Add($"{name} / {key}: forced shown, but the item no longer has that property");
            else if (DisplayedProperties(byName[name!])?.Contains(key) == true)
                stale.Add($"{name} / {key}: forced shown, but the tooltip shows it now");
        }
        foreach (var (name, key) in _selfInflicted)
        {
            if (PropertyOf(name, key) is null)
                stale.Add($"{name} / {key}: marked self-inflicted, but the item no longer has that property");
        }
        foreach (var (name, key) in _forcePerStack)
        {
            if (PropertyOf(name, key) is null)
                stale.Add($"{name} / {key}: forced per stack, but the item no longer has that property");
        }
        foreach (var name in _notSingleTarget)
        {
            if (!byName.TryGetValue(name, out var record) || !HasCastRangeAndNoRadius(record))
                stale.Add($"{name}: marked not single-target, but the game no longer gives it a cast range without a radius");
        }
        foreach (var name in _forceSingleTarget.Keys)
        {
            if (!byName.TryGetValue(name, out var record))
                stale.Add($"{name}: forced single-target, but the game no longer sells it");
            else if (ActiveCastOn(record) is not null)
                stale.Add($"{name}: forced single-target, but the game now gives it a targeted active");
        }
        foreach (var (name, stacks) in _assumedStacks)
        {
            if (!byName.TryGetValue(name, out var record) || !ShownProperties(record).Any(p => IsPerStack(name, p.Key)))
                stale.Add($"{name}: assumed {stacks} stacks, but the item no longer has a per-stack stat");
            else if (PropertyOf(name, "MaxStacks") is not null)
                stale.Add($"{name}: assumed {stacks} stacks, but the game now files its MaxStacks");
        }
        foreach (var (name, record) in byName)
        {
            if (StackCount(record) is not null)
                continue;
            foreach (var (key, _) in ShownProperties(record).Where(p => Stats.ContainsKey(p.Key) && IsPerStack(name, p.Key)))
                stale.Add($"{name} / {key}: counted per stack, but the item has no MaxStacks; add its count to _assumedStacks");
        }
        return stale;
    }

    // -- tooltips ---------------------------------------------------------------------

    /// <summary>A property as the tooltip prints it, or null for one with nothing to show (no label, or a zero: unused cooldowns come as 0).</summary>
    private static TooltipStat? PrintedStat(JsonNode? property)
    {
        if (property is not JsonObject || !JsonRecord.Truthy(JsonRecord.Get(property, "label")))
            return null;
        if (Number(JsonRecord.Get(property, "value")) is not { } number || number == 0)
            return null;
        var prefix = JsonRecord.Text(property, "prefix");
        if (prefix == "{s:sign}")
            prefix = number > 0 ? "+" : "-";
        var value = prefix is "+" or "-" ? prefix + Format.Num(Math.Abs(number)) : Format.Num(number);
        var (spiritScale, boonScale) = Scaling(property);
        return new TooltipStat(
            value + JsonRecord.Text(property, "postfix"),
            JsonRecord.Str(JsonRecord.Get(property, "label")),
            JsonRecord.Contains(property, "usage_flags", "ConditionallyApplied"),
            JsonRecord.Truthy(JsonRecord.Get(property, "negative_attribute")),
            spiritScale,
            boonScale);
    }

    /// <summary>
    /// What each point of spirit power, or each boon, adds to a property. Most properties carry a scale
    /// function that only names what can modify them (cooldown reduction, duration...); the spirit and
    /// boon ones have a stat_scale. The healing ones name their stat in the class instead.
    /// </summary>
    private static (double Spirit, double Boons) Scaling(JsonNode property)
    {
        var function = JsonRecord.Get(property, "scale_function");
        var scale = Number(JsonRecord.Get(function, "stat_scale")) ?? 0;
        return (JsonRecord.Text(function, "specific_stat_scale_type"), JsonRecord.Text(function, "class_name")) switch
        {
            ("ETechPower", _) or (_, "scale_function_healing_spirit_scale") => (scale, 0),
            ("ELevelUpBoons", _) or (_, "scale_function_healing_boon_scale") => (0, scale),
            _ => (0, 0),
        };
    }

    /// <summary>
    /// The item's tooltip sections with every property reference resolved to its printed value.
    /// <paramref name="classToItem"/> maps the API's class names to our item ids, for the components.
    /// </summary>
    public static ItemTooltip? ExtractTooltip(string itemId, JsonNode record, IReadOnlyDictionary<string, string> classToItem)
    {
        var properties = JsonRecord.Get(record, "properties");

        EquatableList<TooltipStat> StatsOf(IEnumerable<string> keys, IReadOnlyDictionary<string, string>? effects = null) =>
            keys.Select(key => effects is not null && effects.TryGetValue(key, out var effect)
                    ? new TooltipStat("", effect)
                    : PrintedStat(JsonRecord.Get(properties, key)))
                .OfType<TooltipStat>()
                .ToEquatableList();

        var sections = new List<TooltipSection>();
        foreach (var raw in JsonRecord.Items(record, "tooltip_sections"))
        {
            // A handful of passives come without a section_type.
            var kind = JsonRecord.Text(raw, "section_type");
            if (kind.Length == 0)
                kind = JsonRecord.Truthy(JsonRecord.Get(record, "is_active_item")) ? "active" : "passive";
            var cooldown = "";
            var blocks = new List<TooltipBlock>();
            foreach (var attribute in JsonRecord.Items(raw, "section_attributes"))
            {
                var keys = JsonRecord.Strings(attribute, "properties").ToList();
                // The cooldown goes in the section's header strip, as in game.
                if (keys.Remove("AbilityCooldown") && PrintedStat(JsonRecord.Get(properties, "AbilityCooldown")) is { } cooldownStat)
                    cooldown = cooldownStat.Value;

                // Status effects (Silenced, Stun...) are boxes with a name and no number.
                var effects = new Dictionary<string, string>();
                foreach (var effect in JsonRecord.Items(attribute, "important_properties_with_icon"))
                {
                    if (effect is JsonObject && JsonRecord.Truthy(JsonRecord.Get(effect, "localized_name")))
                        effects[JsonRecord.Str(JsonRecord.Get(effect, "name"))] = JsonRecord.Str(JsonRecord.Get(effect, "localized_name"));
                }

                var block = new TooltipBlock(
                    TooltipText.From(JsonRecord.Text(attribute, "loc_string")),
                    StatsOf(JsonRecord.Strings(attribute, "elevated_properties")),
                    StatsOf(JsonRecord.Strings(attribute, "important_properties"), effects),
                    StatsOf(keys));
                if (block != TooltipBlock.Empty)
                    blocks.Add(block);
            }
            if (blocks.Count > 0 || cooldown.Length > 0)
                sections.Add(new TooltipSection(kind, cooldown, blocks.ToEquatableList()));
        }

        var components = JsonRecord.Strings(record, "component_items")
            .Where(classToItem.ContainsKey)
            .Select(name => classToItem[name])
            .ToEquatableList();
        if (sections.Count == 0 && components.Count == 0)
            return null;
        return new ItemTooltip(itemId, sections.ToEquatableList(), components);
    }

    // -- applying to the store ----------------------------------------------------------

    /// <summary>
    /// Fold API records into the store in memory. Existing ids are kept, since every other file keys on
    /// them; names, game ids, tiers, shop categories and costs follow the game.
    /// </summary>
    public static SyncReport Apply(DataStore store, IEnumerable<JsonNode?> heroRecords, IEnumerable<JsonNode?> itemRecords)
    {
        var report = new SyncReport();
        var knownItems = store.Items.Values.ToDictionary(item => item.ItemId);
        var heroes = heroRecords.OfType<JsonNode>().ToList();
        ApplyHeroes(store, heroes, report);
        var recordsByItem = ApplyItems(store, itemRecords.OfType<JsonNode>().ToList(), report);
        ApplyStats(store, recordsByItem, knownItems, ShopBonuses(heroes), report);
        ApplyTooltips(store, recordsByItem, report);
        var records = recordsByItem.Values.ToList();
        report.UnmappedStats.AddRange(UnmappedStats(records));
        report.StaleOverrides.AddRange(StaleOverrides(records));
        if (report.AddedHeroes.Count > 0)
            store.SyncCategories();
        ApplyMeasuredMaxHp(store, heroes, report);
        return report;
    }

    // -- hero traits measured from the game ---------------------------------------------

    /// <summary>The trait measured from each hero's health rather than rated by hand.</summary>
    public const string MaxHpTrait = "max_hp";

    /// <summary>
    /// The souls "mid-game" health is read at: 15 level-ups, where the measured scores sit closest to the
    /// hand-rated ones they replaced (r = 0.98, 4 points apart on average).
    /// </summary>
    public const int MidGameSouls = 10_000;

    /// <summary>How far from the roster's median health the ends of the trait's scale sit, as its description says.</summary>
    public const double MaxHpSpread = 0.30;

    /// <summary>Max health a hero's own abilities add, which the hero's stats don't show: Abrams' third ability gives 200.</summary>
    private static readonly Dictionary<string, double> _kitHealth = new() { ["Abrams"] = 200 };

    /// <summary>
    /// A hero's health at <see cref="MidGameSouls"/>: its starting max health, the health every standard
    /// level-up it has reached by then adds, and any <see cref="_kitHealth"/>. Null when the record doesn't say.
    /// </summary>
    public static double? MidGameHealth(JsonNode record)
    {
        if (Number(JsonRecord.Get(JsonRecord.Get(JsonRecord.Get(record, "starting_stats"), "max_health"), "value")) is not { } health)
            return null;
        var perLevel = Number(JsonRecord.Get(JsonRecord.Get(record, "standard_level_up_upgrades"), "MODIFIER_VALUE_BASE_HEALTH_FROM_LEVEL")) ?? 0;
        var levels = (JsonRecord.Get(record, "level_info") as JsonObject ?? [])
            .Count(level => JsonRecord.Truthy(JsonRecord.Get(level.Value, "use_standard_upgrade"))
                            && JsonRecord.Int(level.Value, "required_gold") <= MidGameSouls);
        return health + perLevel * levels + _kitHealth.GetValueOrDefault(NameOf(record) ?? "");
    }

    /// <summary>
    /// Hero id → its <see cref="MaxHpTrait"/> score: its <see cref="MidGameHealth"/> against the median of every
    /// hero the API lists, with ± the trait's top score at ± <see cref="MaxHpSpread"/>.
    /// </summary>
    public static Dictionary<string, double> MeasuredMaxHp(DataStore store, IReadOnlyList<JsonNode> records)
    {
        if (!store.Categories.TryGetValue(MaxHpTrait, out var category))
            return [];
        var byGameId = store.Heroes.Values.Where(hero => hero.GameId != 0).ToDictionary(hero => hero.GameId, hero => hero.HeroId);
        var health = records
            .Select(record => (GameId: JsonRecord.Int(record, "id"), Health: MidGameHealth(record)))
            .Where(entry => entry.Health is > 0)
            .ToList();
        if (health.Count == 0)
            return [];
        var sorted = health.Select(entry => entry.Health!.Value).Order().ToList();
        var median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        return health
            .Where(entry => byGameId.ContainsKey(entry.GameId))
            .ToDictionary(
                entry => byGameId[entry.GameId],
                entry => Math.Round(
                    Math.Clamp((entry.Health!.Value / median - 1) / MaxHpSpread * category.ScaleMax, category.ScaleMin, category.ScaleMax),
                    MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Writes <see cref="MeasuredMaxHp"/> over the hand-rated scores, on profiled heroes only: one rated on
    /// nothing else would count as below average at everything else (<see cref="DataStore.IsProfiled"/>).
    /// </summary>
    private static void ApplyMeasuredMaxHp(DataStore store, IReadOnlyList<JsonNode> records, SyncReport report)
    {
        var measured = records.Where(record => MidGameHealth(record) is not null).Select(NameOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (measured.Count > 0)
            report.StaleOverrides.AddRange(_kitHealth.Keys.Where(name => !measured.Contains(name)).Select(name => $"_kitHealth: {name} isn't among the game's heroes"));
        foreach (var (heroId, score) in MeasuredMaxHp(store, records))
        {
            var key = new ScoreKey(heroId, MaxHpTrait);
            var old = store.HeroScores.GetValueOrDefault(key);
            if (old == score || !store.IsProfiled(heroId))
                continue;
            store.HeroScores[key] = score;
            report.MeasuredChanges.Add($"{store.Heroes[heroId].HeroName}: {Format.Num(old)} -> {Format.Num(score)}");
        }
    }

    /// <summary>
    /// Finds our row for an API record. The game id comes first: it survives the game renaming the
    /// hero or item, which a name match would take for a new row, orphaning the old one's rules. A
    /// name only matches a row whose game id the game no longer uses (or that has none yet), so a new
    /// record that takes over a renamed row's old name can't claim that row too.
    /// </summary>
    private sealed class RowLookup<T> where T : class
    {
        private readonly Dictionary<long, T> _byGameId = [];
        private readonly Dictionary<string, T> _byName = [];

        public RowLookup(IEnumerable<T> rows, Func<T, long> gameIdOf, Func<T, IEnumerable<string>> namesOf, IEnumerable<JsonNode> records)
        {
            var gameIds = records.Select(record => JsonRecord.Int(record, "id")).ToHashSet();
            foreach (var row in rows)
            {
                var gameId = gameIdOf(row);
                if (gameId != 0 && gameIds.Contains(gameId))
                {
                    _byGameId.TryAdd(gameId, row);
                    continue;
                }
                foreach (var name in namesOf(row))
                    _byName.TryAdd(Norm(name), row);
            }
        }

        public T? Find(long gameId, string name) =>
            _byGameId.TryGetValue(gameId, out var row) ? row : _byName.GetValueOrDefault(Norm(name));
    }

    /// <summary>The game's name for a row, unless ours only spells it differently ("Doorman" for "The Doorman").</summary>
    private static string FollowName(string ours, string game) => Norm(ours) == Norm(game) ? ours : game;

    private static void ApplyHeroes(DataStore store, List<JsonNode> records, SyncReport report)
    {
        var ours = new RowLookup<Hero>(store.Heroes.Values, hero => hero.GameId, hero => [hero.HeroName, hero.HeroId], records);

        foreach (var record in records)
        {
            var name = JsonRecord.Text(record, "name");
            var gameId = JsonRecord.Int(record, "id");
            if (ours.Find(gameId, name) is not { } hero)
            {
                var heroId = MakeId(name);
                if (heroId.Length == 0 || store.Heroes.ContainsKey(heroId))
                    continue;
                store.Heroes[heroId] = new Hero(heroId, name, gameId);
                report.AddedHeroes.Add(name);
                report.HeroesChanged = true;
                continue;
            }

            var updated = hero with { HeroName = FollowName(hero.HeroName, name), GameId = gameId };
            if (updated == hero)
                continue;
            if (updated.HeroName != hero.HeroName)
                report.Changed.Add($"{hero.HeroName}: renamed to {updated.HeroName}");
            if (hero.GameId != 0 && hero.GameId != gameId)
                report.Changed.Add($"{hero.HeroName}: game id {hero.GameId} -> {gameId}");
            else if (hero.GameId == 0)
                report.Filled++;
            store.Heroes[hero.HeroId] = updated;
            report.HeroesChanged = true;
        }
    }

    /// <returns>Item id → API record, for every item the game still sells.</returns>
    private static OrderedDictionary<string, JsonNode> ApplyItems(DataStore store, List<JsonNode> records, SyncReport report)
    {
        var ours = new RowLookup<Item>(store.Items.Values, item => item.GameId, item => [item.ItemName], records);
        var matched = new OrderedDictionary<string, JsonNode>();

        foreach (var record in records)
        {
            var name = JsonRecord.Text(record, "name");
            var gameId = JsonRecord.Int(record, "id");
            var tier = (int)JsonRecord.Int(record, "item_tier");
            var category = JsonRecord.Text(record, "item_slot_type");
            var cost = (int)JsonRecord.Int(record, "cost");
            var castOn = CastOn(record);

            if (ours.Find(gameId, name) is not { } current)
            {
                var itemId = MakeId(name);
                if (itemId.Length == 0 || store.Items.ContainsKey(itemId))
                    continue;
                store.Items[itemId] = new Item(itemId, name, category, tier, gameId, cost, castOn);
                matched[itemId] = record;
                report.AddedItems.Add($"{name} (T{tier})");
                if (castOn is not null)
                    report.TargetingChanges.Add($"{name}: {Targeting(castOn)}");
                report.ItemsChanged = true;
                continue;
            }

            matched[current.ItemId] = record;
            var updated = new Item(current.ItemId, FollowName(current.ItemName, name), category, tier, gameId, cost, castOn);
            if (updated == current)
                continue;
            if (current.CastOn != castOn)
                report.TargetingChanges.Add($"{current.ItemName}: {Targeting(castOn)}");
            // A missing game id or cost is just the first sync filling columns in; a rename, tier or
            // shop move is a patch, and worth saying out loud.
            var before = report.Changed.Count;
            if (updated.ItemName != current.ItemName)
                report.Changed.Add($"{current.ItemName}: renamed to {updated.ItemName}");
            if (current.Tier != tier)
                report.Changed.Add($"{current.ItemName}: tier {current.Tier} -> {tier}");
            if (current.Category != category)
                report.Changed.Add($"{current.ItemName}: shop {current.Category} -> {category}");
            if (current.Cost != 0 && current.Cost != cost)
                report.Changed.Add($"{current.ItemName}: cost {current.Cost} -> {cost}");
            if (current.GameId != 0 && current.GameId != gameId)
                report.Changed.Add($"{current.ItemName}: game id {current.GameId} -> {gameId}");
            if (report.Changed.Count == before && updated with { CastOn = current.CastOn } != current)
                report.Filled++;
            store.Items[current.ItemId] = updated;
            report.ItemsChanged = true;
        }

        report.NotInGame.AddRange(store.Items.Keys.Where(itemId => !matched.ContainsKey(itemId)).Select(itemId => store.Items[itemId].ItemName));
        return matched;
    }

    private static string Targeting(Relation? castOn) => castOn switch
    {
        Relation.Against => "single-target, cast on an enemy",
        Relation.With => "single-target, cast on an ally",
        _ => "no longer single-target",
    };

    /// <param name="knownItems">The items before this sync: a new item's stats are news, not changes.</param>
    private static void ApplyStats(
        DataStore store, OrderedDictionary<string, JsonNode> recordsByItem, IReadOnlyDictionary<string, Item> knownItems,
        IReadOnlyDictionary<string, ShopBonus> shopBonuses, SyncReport report)
    {
        var fresh = new OrderedDictionary<string, List<ItemStat>>();
        foreach (var (itemId, record) in recordsByItem)
        {
            var stats = ExtractStats(itemId, record, shopBonuses);
            if (stats.Count > 0)
                fresh[itemId] = stats;
        }
        report.StatRows = fresh.Values.Sum(stats => stats.Count);
        var same = fresh.Count == store.ItemStats.Count
                   && fresh.All(pair => store.ItemStats.TryGetValue(pair.Key, out var old) && old.SequenceEqual(pair.Value));
        if (same)
            return;
        // With no stats on file there's nothing to compare against: every row would read as new.
        if (store.ItemStats.Count > 0)
        {
            var known = recordsByItem.Keys.Where(knownItems.ContainsKey).ToList();
            foreach (var itemId in known)
                report.StatChanges.AddRange(StatChanges(store.Items[itemId].ItemName, store.ItemStats.GetValueOrDefault(itemId), fresh.GetValueOrDefault(itemId)));
            report.ShopBonusChanges.AddRange(ShopBonusChanges(store.ItemStats, known.Select(itemId => knownItems[itemId]), shopBonuses));
        }
        store.ItemStats = fresh;
        store.RebuildDerived();
        report.StatsChanged = true;
    }

    /// <summary>"Long Range: Weapon Damage (conditional) none -> 40%", one line per stat that moved.</summary>
    private static IEnumerable<string> StatChanges(string itemName, IReadOnlyList<ItemStat>? before, IReadOnlyList<ItemStat>? after)
    {
        var old = (before ?? []).ToDictionary(stat => (stat.Stat, stat.Conditional));
        var now = (after ?? []).ToDictionary(stat => (stat.Stat, stat.Conditional));
        foreach (var key in old.Keys.Concat(now.Keys).Distinct().OrderBy(key => key.Stat, StringComparer.Ordinal).ThenBy(key => key.Conditional))
        {
            // A shop bonus only moves with the item's cost or shop, already reported, or with the whole curve (ShopBonusChanges).
            if (_shopBonusStatNames.Contains(key.Stat))
                continue;
            old.TryGetValue(key, out var was);
            now.TryGetValue(key, out var becomes);
            if (was?.Value == becomes?.Value)
                continue;
            var stat = becomes ?? was!;
            var name = stat.Label + (stat.Conditional ? " (conditional)" : "");
            yield return $"{itemName}: {name} {StatValue(was)} -> {StatValue(becomes)}";
        }
    }

    private static string StatValue(ItemStat? stat) => stat switch
    {
        null => "none",
        { Unit: "" } => Format.Num(stat.Value),
        { Unit: "%" } => Format.Num(stat.Value) + "%",
        _ => $"{Format.Num(stat.Value)} {stat.Unit}",
    };

    /// <summary>
    /// "weapon: Weapon Damage (shop bonus) 115% at 28800 souls", one line per shop whose curve moved,
    /// instead of one per item in it. An item's old share is checked against its old cost under the new
    /// curve, so a cost change alone doesn't count.
    /// </summary>
    private static IEnumerable<string> ShopBonusChanges(
        IReadOnlyDictionary<string, List<ItemStat>> oldStats, IEnumerable<Item> knownItems, IReadOnlyDictionary<string, ShopBonus> shopBonuses)
    {
        var items = knownItems.ToList();
        foreach (var (shop, stat) in ShopBonusStats)
        {
            var bonus = shopBonuses.GetValueOrDefault(shop);
            var moved = items.Where(item => item.Category == shop).Any(item =>
                (oldStats.GetValueOrDefault(item.ItemId)?.FirstOrDefault(old => old.Stat == stat.Stat)?.Value ?? 0)
                != (bonus?.Share(item.Cost) ?? 0));
            if (!moved)
                continue;
            yield return bonus is null
                ? $"{shop}: the game data has no shop bonus any more"
                : $"{shop}: {stat.Label} {Format.Num(bonus.Bonus)}{stat.Unit} at {Format.Num(bonus.Souls)} souls";
        }
    }

    private static void ApplyTooltips(DataStore store, OrderedDictionary<string, JsonNode> recordsByItem, SyncReport report)
    {
        var classToItem = new Dictionary<string, string>();
        foreach (var (itemId, record) in recordsByItem)
            classToItem[JsonRecord.Str(JsonRecord.Get(record, "class_name"))] = itemId;

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
        // Hand-typed rules encode what the tooltip says, so a changed tooltip is the cue to recheck them.
        foreach (var (itemId, tooltip) in fresh)
        {
            if (store.ItemTooltips.TryGetValue(itemId, out var old) && old != tooltip && store.RuleCount(itemId) > 0)
                report.ReviewRules.Add(store.Items[itemId].ItemName);
        }
        store.ItemTooltips = fresh;
        report.TooltipsChanged = true;
    }

    /// <summary>Write only the files the sync actually changed, so a no-op sync doesn't churn backups.</summary>
    public static void SaveSynced(DataStore store, SyncReport report)
    {
        if (report.HeroesChanged)
            store.SaveHeroes();
        if (report.AddedHeroes.Count > 0 || report.HeroScoresChanged)
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

    [GeneratedRegex(@"^" + SyncText.Space + @"*(-?[0-9]+(?:\.[0-9]+)?)")]
    private static partial Regex LeadingNumber();
}
