namespace DeadlockAdvisor.Services.GameApi;

/// <summary>What a game sync changed, and the lines the report modal shows.</summary>
public sealed class SyncReport
{
    public List<string> AddedHeroes { get; } = [];
    public List<string> AddedItems { get; } = [];

    /// <summary>Human-readable field changes: "Headshot Booster: tier 4 -> 1".</summary>
    public List<string> Changed { get; } = [];

    /// <summary>Rows whose empty game_id / cost columns got filled in.</summary>
    public int Filled { get; set; }

    /// <summary>Our items the API doesn't sell.</summary>
    public List<string> NotInGame { get; } = [];

    public bool HeroesChanged { get; set; }
    public bool ItemsChanged { get; set; }
    public bool StatsChanged { get; set; }
    public int StatRows { get; set; }
    public bool TooltipsChanged { get; set; }
    public int TooltipCount { get; set; }

    public bool AnythingChanged => HeroesChanged || ItemsChanged || StatsChanged || TooltipsChanged;

    public List<string> Lines()
    {
        var lines = new List<string>();
        if (AddedHeroes.Count > 0)
            lines.Add($"Added {AddedHeroes.Count} hero(es): {string.Join(", ", AddedHeroes)}");
        if (AddedItems.Count > 0)
            lines.Add($"Added {AddedItems.Count} item(s): {string.Join(", ", AddedItems)}");
        if (Filled > 0)
            lines.Add($"Filled in game ids / costs on {Filled} hero and item row(s).");
        if (Changed.Count > 0)
        {
            lines.Add($"Updated {Changed.Count} field(s):");
            lines.AddRange(Changed.Select(line => $"  {line}"));
        }
        if (StatsChanged)
            lines.Add($"Item stats refreshed: {StatRows} stat row(s).");
        if (TooltipsChanged)
            lines.Add($"Item tooltips refreshed: {TooltipCount} item(s).");
        if (NotInGame.Count > 0)
        {
            lines.Add($"{NotInGame.Count} item(s) in items.csv aren't in the game's shop "
                      + "(left alone -- delete them by hand if they were removed): "
                      + string.Join(", ", NotInGame));
        }
        if (!AnythingChanged)
            lines.Add("Everything already matches the game -- nothing changed.");
        return lines;
    }
}
