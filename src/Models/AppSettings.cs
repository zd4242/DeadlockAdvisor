using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Models;

public class AppSettings
{
    // Where data/ and assets/ live. Null means the default folder under %AppData%.
    public string? DataRoot { get; set; }

    // Asked once, on a first run with no art, whether to download it.
    public bool ArtDownloadOffered { get; set; }

    // Preferences (the Settings page)
    public bool ReopenLastPage { get; set; } = true;
    public bool ReopenLastMatch { get; set; } = true;
    public bool CheckForNewerPatch { get; set; } = true;
    public bool MinimizeToDetect { get; set; } = true;
    public bool KeepUnreadCaptures { get; set; } = true;

    // View
    public int ZoomIndex { get; set; } = ZoomLevels.DefaultIndex;
    public WindowGeometry? WindowGeometry { get; set; }
    public int LastPage { get; set; }

    // Match tab
    public SavedMatch? LastMatch { get; set; }
    public int ResultsMinPercent { get; set; } = 40;

    [JsonConverter(typeof(JsonStringEnumConverter<RankBy>))]
    public RankBy ResultsRankBy { get; set; }

    public bool ResultsByTier { get; set; }
    public bool ResultsByNetWorth { get; set; } = true;

    // Item Formulas tab
    public double? ByItemSplitterPosition { get; set; }

    // Screen detection: the top-bar grid last found, keyed by screen size ("2560x1440").
    public Dictionary<string, JsonObject> VisionGeometry { get; set; } = [];
}

public class WindowGeometry
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}

/// <summary>The match you had set up, so the app reopens on it. Roles keep their order.</summary>
public class SavedMatch
{
    public OrderedDictionary<string, string> Roles { get; set; } = [];

    /// <summary>Hero → top-bar slot, for heroes a detection placed.</summary>
    public Dictionary<string, int> Slots { get; set; } = [];

    public List<SavedNetWorth> NetWorth { get; set; } = [];
}

public class SavedNetWorth
{
    public DateTimeOffset At { get; set; }
    public Dictionary<string, int> Souls { get; set; } = [];
}
