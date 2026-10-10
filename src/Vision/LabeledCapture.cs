using System.Text.Json.Nodes;

namespace DeadlockAdvisor.Vision;

/// <summary>How a portrait was drawn when it was captured, which decides how hard it is to read.</summary>
public enum SlotState
{
    Visible,

    /// <summary>Out of your team's sight: drawn see-through and washed out over the game world.</summary>
    Faded,

    /// <summary>Low health: a red backdrop, CRITICAL across it, and the hero's critical art.</summary>
    Critical,

    /// <summary>On a kill streak ("on fire"): the hero's gloat art.</summary>
    Gloat,

    /// <summary>A black silhouette under a respawn timer, or nothing at all: none of the hero shows.</summary>
    Dead,

    /// <summary>An ultimate that changes how the hero looks (Silver's wolf form), which no reference art shows.</summary>
    Transformed,

    /// <summary>Covered by something else on screen, such as a chat bubble or an ability callout.</summary>
    Occluded,
}

/// <summary>Where a slot's label came from, when it isn't simply a confident read that was applied.</summary>
public enum LabelSource
{
    Read,

    /// <summary>Applied as read although the read wasn't confident: probably right, but unconfirmed.</summary>
    Unsure,

    Corrected,

    /// <summary>Not read off the portrait, but kept from the match already applied.</summary>
    Kept,
}

/// <param name="Hero">The hero the slot was read as, or null.</param>
public sealed record SlotRead(string? Hero, double Score, double Margin, bool Confident);

/// <summary>
/// A capture and what's in it: the hero in each slot, how each portrait was drawn, and, for one
/// kept by Detect, what the detector made of it. The format is the test fixtures', so a kept capture
/// can become a fixture by copying it. Slots without a hero are ones nobody could name.
/// </summary>
public sealed class LabeledCapture
{
    public List<string> Notes { get; init; } = [];
    public int? ScreenWidth { get; init; }
    public int? ScreenHeight { get; init; }

    /// <summary>Which match it's from, so evaluation can hold a whole match out.</summary>
    public string? Match { get; init; }

    /// <summary>Pregame, laning, mid, late or spectating: the top bar looks different in each.</summary>
    public string? Phase { get; init; }

    public int? SelfSlot { get; set; }
    public int AllowWrong { get; init; }
    public Dictionary<int, string> Heroes { get; init; } = [];
    public Dictionary<int, SlotState> States { get; init; } = [];
    public Dictionary<int, LabelSource> Sources { get; init; } = [];

    /// <summary>False when it was applied without anyone looking at it.</summary>
    public bool Reviewed { get; init; } = true;

    /// <summary>The grid it was read with, so evaluation can skip the search.</summary>
    public Geometry? Grid { get; init; }

    /// <summary>What the detector said, before anyone corrected it.</summary>
    public IReadOnlyList<SlotRead>? Read { get; init; }

    public int? ReadSelfSlot { get; init; }

    public SlotState StateOf(int slot) => States.GetValueOrDefault(slot, SlotState.Visible);

    public LabelSource SourceOf(int slot) => Sources.GetValueOrDefault(slot, LabelSource.Read);

    /// <summary>An applied detection: what was applied, and what the detector read before it was reviewed.</summary>
    /// <param name="heroes">The hero applied to each slot, or null.</param>
    /// <param name="corrected">The slots whose hero was changed in the review.</param>
    public static LabeledCapture FromApplied(Detection detection, IReadOnlyList<string?> heroes, int? selfSlot,
        IReadOnlyCollection<int> corrected, bool reviewed, int screenWidth, int screenHeight)
    {
        var labels = new Dictionary<int, string>();
        var sources = new Dictionary<int, LabelSource>();
        for (var slot = 0; slot < heroes.Count; slot++)
        {
            if (heroes[slot] is not { } hero)
                continue;
            labels[slot] = hero;
            if (corrected.Contains(slot))
                sources[slot] = LabelSource.Corrected;
            else if (detection.Slots[slot].Kept)
                sources[slot] = LabelSource.Kept;
            else if (!detection.Slots[slot].IsConfident)
                sources[slot] = LabelSource.Unsure;
        }
        return new LabeledCapture
        {
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight,
            SelfSlot = selfSlot,
            Heroes = labels,
            Sources = sources,
            Reviewed = reviewed,
            Grid = detection.Geometry,
            Read = detection.Slots.Select(slot => new SlotRead(slot.HeroId, slot.Score, slot.Margin, slot.IsConfident)).ToList(),
            ReadSelfSlot = detection.SelfSlot,
        };
    }

    public JsonObject ToJson()
    {
        var json = new JsonObject();
        if (Notes.Count > 0)
            json["notes"] = new JsonArray(Notes.Select(line => (JsonNode?)line).ToArray());
        if (ScreenWidth is { } width)
            json["screen_width"] = width;
        if (ScreenHeight is { } height)
            json["screen_height"] = height;
        if (Match is not null)
            json["match"] = Match;
        if (Phase is not null)
            json["phase"] = Phase;
        if (SelfSlot is { } self)
            json["self_slot"] = self;
        json["allow_wrong"] = AllowWrong;
        json["heroes"] = BySlot(Heroes, hero => hero);
        if (States.Count > 0)
            json["states"] = BySlot(States, state => Name(state));
        if (Sources.Count > 0)
            json["sources"] = BySlot(Sources, source => Name(source));
        if (!Reviewed)
            json["reviewed"] = false;
        if (Grid is not null)
            json["grid"] = Grid.ToJson();
        if (Read is not null)
        {
            json["read"] = new JsonObject
            {
                ["self_slot"] = ReadSelfSlot,
                ["slots"] = new JsonArray(Read.Select(slot => (JsonNode?)new JsonObject
                {
                    ["hero"] = slot.Hero,
                    ["score"] = Math.Round(slot.Score, 4),
                    ["margin"] = Math.Round(slot.Margin, 4),
                    ["confident"] = slot.Confident,
                }).ToArray()),
            };
        }
        return json;
    }

    public static LabeledCapture FromJson(JsonNode json)
    {
        var read = json["read"]?["slots"] as JsonArray;
        return new LabeledCapture
        {
            Notes = (json["notes"] as JsonArray)?.Select(line => (string?)line ?? "").ToList() ?? [],
            ScreenWidth = (int?)json["screen_width"],
            ScreenHeight = (int?)json["screen_height"],
            Match = (string?)json["match"],
            Phase = (string?)json["phase"],
            SelfSlot = (int?)json["self_slot"],
            AllowWrong = (int?)json["allow_wrong"] ?? 0,
            Heroes = Entries(json["heroes"]).ToDictionary(),
            States = Slots<SlotState>(json["states"]),
            Sources = Slots<LabelSource>(json["sources"]),
            Reviewed = (bool?)json["reviewed"] ?? true,
            // Whichever search found it, it's where this capture's heroes are.
            Grid = json["grid"] is JsonObject grid ? Geometry.Parse(grid) : null,
            Read = read?.Select(slot => new SlotRead((string?)slot!["hero"], (double?)slot["score"] ?? 0, (double?)slot["margin"] ?? 0,
                (bool?)slot["confident"] ?? false)).ToList(),
            ReadSelfSlot = (int?)json["read"]?["self_slot"],
        };
    }

    private static JsonObject BySlot<T>(Dictionary<int, T> values, Func<T, string> text)
    {
        var json = new JsonObject();
        foreach (var (slot, value) in values.OrderBy(pair => pair.Key))
            json[slot.ToString(System.Globalization.CultureInfo.InvariantCulture)] = text(value);
        return json;
    }

    /// <summary>A {"slot": "text"} object's entries; anything else in it is skipped.</summary>
    private static IEnumerable<KeyValuePair<int, string>> Entries(JsonNode? node)
    {
        if (node is not JsonObject slots)
            yield break;
        foreach (var (key, item) in slots)
        {
            if (int.TryParse(key, System.Globalization.CultureInfo.InvariantCulture, out var slot) && item is JsonValue value
                && value.TryGetValue<string>(out var text))
                yield return new(slot, text);
        }
    }

    private static Dictionary<int, T> Slots<T>(JsonNode? node) where T : struct, Enum
    {
        var result = new Dictionary<int, T>();
        foreach (var (slot, text) in Entries(node))
        {
            if (Enum.TryParse<T>(text.Replace("_", ""), ignoreCase: true, out var value))
                result[slot] = value;
        }
        return result;
    }

    private static string Name<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
}
