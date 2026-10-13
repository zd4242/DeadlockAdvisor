using System.Text.Json;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Vision;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Tests.Support;

/// <summary>
/// What a hero's cells hold, as a person saw them, one string a cell in order: <c>weapon:active_reload</c> (an item known by
/// name), <c>weapon</c> (an item in the shop category, not named), <c>hidden</c> (a question mark) or <c>unseen</c> (something is
/// over it). Heroes a label doesn't list hold nothing.
/// </summary>
public sealed record HeroLabel(IReadOnlyList<string> Cells, bool Covered = false)
{
    /// <summary>A cell's label as its kind, category, item and tier; a named item's tier is its own, an unnamed one may add it as <c>weapon@2</c>.</summary>
    public static (string Kind, ShopCategory? Category, string? ItemId, int? Tier) Parse(string cell)
    {
        if (cell is "hidden" or "unseen")
            return (cell, null, null, null);
        var tier = cell.Split('@', 2) is { Length: 2 } tiered ? int.Parse(tiered[1], System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
        var parts = cell.Split('@')[0].Split(':', 2);
        var category = ItemTemplates.CategoryOf(parts[0]) ?? throw new FormatException($"Not a cell: {cell}");
        return ("item", category, parts.Length > 1 ? parts[1] : null, tier);
    }

    public static string Format(CellRead read) => read.Kind switch
    {
        CellKind.Hidden => "hidden",
        CellKind.Unseen => "unseen",
        _ => read.Category.ToString()!.ToLowerInvariant() + (read.ItemId is { } id ? ":" + id : read.Tier is { } tier ? "@" + tier : ""),
    };
}

/// <summary>
/// A capture of the scoreboard and what is on it: the grid its portraits were found with, and every hero's cells. The image
/// is kept with everything outside the heroes' columns and above their bars painted black, so no player name is in it.
/// </summary>
public sealed class ItemBoardLabel
{
    private static readonly JsonSerializerOptions _indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public List<string> Notes { get; init; } = [];
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }

    /// <summary>Which match it is from.</summary>
    public string? Match { get; init; }

    /// <summary>Where in a match: early, mid, late, or what is over the board.</summary>
    public string? Phase { get; init; }

    public Geometry Grid { get; init; } = new(0, 0, 0);

    /// <summary>The slots a Street Brawl leaves blank, which aren't read.</summary>
    public List<int> BlankSlots { get; init; } = [];

    public Dictionary<int, HeroLabel> Heroes { get; init; } = [];

    public HeroLabel HeroAt(int slot) => Heroes.GetValueOrDefault(slot) ?? new HeroLabel([]);

    public JsonObject ToJson()
    {
        var json = new JsonObject();
        if (Notes.Count > 0)
            json["notes"] = new JsonArray(Notes.Select(line => (JsonNode?)line).ToArray());
        json["screen_width"] = ScreenWidth;
        json["screen_height"] = ScreenHeight;
        if (Match is not null)
            json["match"] = Match;
        if (Phase is not null)
            json["phase"] = Phase;
        json["grid"] = Grid.ToJson();
        if (BlankSlots.Count > 0)
            json["blank_slots"] = new JsonArray(BlankSlots.Select(slot => (JsonNode?)slot).ToArray());
        var heroes = new JsonObject();
        foreach (var (slot, hero) in Heroes.OrderBy(pair => pair.Key))
        {
            var entry = new JsonObject();
            if (hero.Covered)
                entry["covered"] = true;
            entry["cells"] = new JsonArray(hero.Cells.Select(cell => (JsonNode?)cell).ToArray());
            heroes[slot.ToString(System.Globalization.CultureInfo.InvariantCulture)] = entry;
        }
        json["heroes"] = heroes;
        return json;
    }

    public string ToText() => ToJson().ToJsonString(_indented) + "\n";

    public static ItemBoardLabel FromJson(JsonNode json) => new()
    {
        Notes = (json["notes"] as JsonArray)?.Select(line => (string?)line ?? "").ToList() ?? [],
        ScreenWidth = (int)json["screen_width"]!,
        ScreenHeight = (int)json["screen_height"]!,
        Match = (string?)json["match"],
        Phase = (string?)json["phase"],
        Grid = Geometry.Parse(json["grid"]!.AsObject())!,
        BlankSlots = (json["blank_slots"] as JsonArray)?.Select(slot => (int)slot!).ToList() ?? [],
        Heroes = json["heroes"]!.AsObject().ToDictionary(
            pair => int.Parse(pair.Key, System.Globalization.CultureInfo.InvariantCulture),
            pair => new HeroLabel(pair.Value!["cells"]!.AsArray().Select(cell => (string)cell!).ToList(), (bool?)pair.Value["covered"] ?? false)),
    };
}

/// <summary>A labelled board and its image, from a folder of both.</summary>
public sealed record ItemCorpusBoard(string Name, ItemBoardLabel Label, RgbImage Image)
{
    public static List<ItemCorpusBoard> LoadAll(string folder) =>
        Directory.GetFiles(folder, "*.json")
            .Order(StringComparer.Ordinal)
            .Where(json => File.Exists(Path.ChangeExtension(json, ".png")))
            .Select(json => new ItemCorpusBoard(Path.GetFileNameWithoutExtension(json),
                ItemBoardLabel.FromJson(JsonNode.Parse(File.ReadAllText(json))!), ImageFile.Load(Path.ChangeExtension(json, ".png"))))
            .ToList();
}
