using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DeadlockAdvisor.Core;

namespace DeadlockAdvisor.Models;

/// <summary>
/// One number the way the game's tooltip prints it: "-24%" "Move Speed". Display-only; scoring
/// reads <see cref="ItemStat"/>, never this.
/// </summary>
/// <param name="Negative">A drawback, e.g. Weighted Shots' -0.5 m Move Speed.</param>
/// <param name="SpiritScale">What each point of spirit power adds to the value, e.g. Scourge's 0.0055; 0 when it doesn't scale.</param>
public sealed record TooltipStat(string Value, string Label, bool Conditional = false, bool Negative = false, double SpiritScale = 0);

/// <summary>
/// A description and the numbers printed under it. <see cref="Elevated"/> is the big headline stat,
/// <see cref="Important"/> get a box each, <see cref="Stats"/> share one.
/// </summary>
/// <param name="Text">The API's HTML cut down to &lt;b&gt;, &lt;i&gt;, &lt;br&gt; and coloured spans.</param>
public sealed record TooltipBlock(
    string Text,
    EquatableList<TooltipStat> Elevated,
    EquatableList<TooltipStat> Important,
    EquatableList<TooltipStat> Stats)
{
    public static readonly TooltipBlock Empty = new("", EquatableList<TooltipStat>.Empty,
        EquatableList<TooltipStat>.Empty, EquatableList<TooltipStat>.Empty);
}

/// <param name="Kind">innate / passive / active.</param>
public sealed record TooltipSection(string Kind, string Cooldown, EquatableList<TooltipBlock> Blocks);

/// <param name="Components">The item ids this one is built from.</param>
public sealed partial record ItemTooltip(
    string ItemId,
    EquatableList<TooltipSection> Sections,
    EquatableList<string> Components)
{
    /// <summary>
    /// Everything the card says (descriptions without their markup, then each stat's value and
    /// label), lowercased for text search.
    /// </summary>
    public string SearchText { get; } = BuildSearchText(Sections);

    private static string BuildSearchText(IEnumerable<TooltipSection> sections)
    {
        var parts = new List<string>();
        foreach (var section in sections)
        {
            parts.Add(section.Kind);
            foreach (var block in section.Blocks)
            {
                var text = LineBreakPattern().Replace(block.Text, " ");
                parts.Add(WebUtility.HtmlDecode(TagPattern().Replace(text, "")));
                foreach (var stat in block.Elevated.Concat(block.Important).Concat(block.Stats))
                    parts.Add($"{stat.Value} {stat.Label}");
            }
        }

        var words = string.Join(" ", parts).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words).ToLowerInvariant();
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakPattern();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagPattern();

    /// <summary>The JSON form, keyed by item id in the file, so the id itself is left out.</summary>
    public JsonObject ToJson() => new()
    {
        ["sections"] = new JsonArray(Sections.Select(section => (JsonNode)new JsonObject
        {
            ["kind"] = section.Kind,
            ["cooldown"] = section.Cooldown,
            ["blocks"] = new JsonArray(section.Blocks.Select(block => (JsonNode)new JsonObject
            {
                ["text"] = block.Text,
                ["elevated"] = StatsToJson(block.Elevated),
                ["important"] = StatsToJson(block.Important),
                ["stats"] = StatsToJson(block.Stats),
            }).ToArray()),
        }).ToArray()),
        ["components"] = new JsonArray(Components.Select(component => (JsonNode)JsonValue.Create(component)).ToArray()),
    };

    private static JsonArray StatsToJson(IEnumerable<TooltipStat> stats) =>
        new(stats.Select(stat =>
        {
            var json = new JsonObject
            {
                ["value"] = stat.Value,
                ["label"] = stat.Label,
                ["conditional"] = stat.Conditional,
                ["negative"] = stat.Negative,
            };
            // Most stats don't scale, so the key is left out rather than written as 0 everywhere.
            if (stat.SpiritScale != 0)
                json["spirit_scale"] = stat.SpiritScale;
            return (JsonNode)json;
        }).ToArray());

    public static ItemTooltip FromJson(string itemId, JsonObject data)
    {
        var sections = Children(data, "sections").Select(section => new TooltipSection(
            Text(section, "kind"),
            Text(section, "cooldown"),
            Children(section, "blocks").Select(block => new TooltipBlock(
                Text(block, "text"),
                StatsFromJson(block, "elevated"),
                StatsFromJson(block, "important"),
                StatsFromJson(block, "stats"))).ToEquatableList())).ToEquatableList();

        var components = (data["components"] as JsonArray ?? [])
            .Select(component => component!.GetValue<string>())
            .ToEquatableList();

        return new ItemTooltip(itemId, sections, components);
    }

    private static IEnumerable<JsonObject> Children(JsonObject parent, string key) =>
        (parent[key] as JsonArray ?? []).OfType<JsonObject>();

    private static string Text(JsonObject parent, string key) =>
        parent[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static bool Flag(JsonObject parent, string key) =>
        parent[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static double Number(JsonObject parent, string key) =>
        parent[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;

    private static EquatableList<TooltipStat> StatsFromJson(JsonObject block, string key) =>
        Children(block, key)
            .Select(stat => new TooltipStat(Text(stat, "value"), Text(stat, "label"), Flag(stat, "conditional"), Flag(stat, "negative"),
                Number(stat, "spirit_scale")))
            .ToEquatableList();
}
