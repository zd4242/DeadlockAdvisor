using System.Text.Json.Nodes;
using DeadlockAdvisor.Core;

namespace DeadlockAdvisor.Models;

public class AppSettings
{
    // Where data/ and assets/ live. Null means the default folder under %AppData%.
    public string? DataRoot { get; set; }

    // Asked once, on a first run with no art, whether to download it.
    public bool ArtDownloadOffered { get; set; }

    // View
    public int ZoomIndex { get; set; } = ZoomLevels.DefaultIndex;
    public WindowGeometry? WindowGeometry { get; set; }
    public int LastPage { get; set; }

    // Match tab
    public SavedMatch? LastMatch { get; set; }
    public int ResultsMinPercent { get; set; } = 40;
    public bool ResultsByTier { get; set; }

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
    public List<string> Lane { get; set; } = [];
}
