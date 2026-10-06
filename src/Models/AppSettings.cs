using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Scoring;

namespace DeadlockAdvisor.Models;

public class AppSettings
{
    // Where data/ and assets/ live. Null means the default folder under %AppData%.
    public string? DataRoot { get; set; }

    // Shown once, on a first run with no art: the offer of art and match data. Saved under its old
    // name, from when it offered only art, so it isn't shown again after an update.
    [JsonPropertyName("ArtDownloadOffered")]
    public bool WelcomeOffered { get; set; }

    // When the art was last checked against deadlock-api.com's, which is done about weekly.
    public DateTimeOffset? ArtCheckedAt { get; set; }

    // Whether the last match data download took the rank groups too: the next one offers the same.
    public bool MatchDataIncludeRanks { get; set; }

    // Refresh the match data in the background on startup when a newer patch is out or it's gone stale.
    public bool AutoUpdateMatchData { get; set; } = true;

    // Take a newer published model (hero ratings, item formulas) on startup: files changed here are asked about.
    public bool AutoUpdateModel { get; set; } = true;

    // Say in the status bar when a newer version of the app is out, unless it's the one dismissed there.
    public bool CheckForAppUpdates { get; set; } = true;
    public string? SkippedAppVersion { get; set; }

    // The version that ran last, so the first start after an update can say so.
    public string? LastRunVersion { get; set; }

    // When each update check last got an answer, for Settings → Data to show.
    public DateTimeOffset? MatchDataCheckedAt { get; set; }
    public DateTimeOffset? ModelCheckedAt { get; set; }
    public DateTimeOffset? AppUpdateCheckedAt { get; set; }

    // How the last match data downloads went, for the next one's estimate; null until one has run.
    public double? MatchFetchSecondsPerCall { get; set; }
    public double? MatchFetchBytesPerCall { get; set; }

    // Preferences (the Settings page)
    public bool ReopenLastPage { get; set; } = true;
    public bool ReopenLastMatch { get; set; } = true;
    public bool ShowRandomButtons { get; set; } = true;
    public bool ShowExplainMath { get; set; } = true;
    public bool ShowModelEditors { get; set; }
    public bool CheckForNewerPatch { get; set; } = true;
    public bool DetectFromAnywhere { get; set; } = true;
    public bool ComeUpForReview { get; set; }
    public bool MinimizeToDetect { get; set; } = true;
    public bool KeepUnreadCaptures { get; set; } = true;
    public bool KeepDetectionCaptures { get; set; } = true;
    public bool RememberCorrections { get; set; } = true;
    public bool AutoApplyDetect { get; set; } = true;

    // Your Steam account ID (SteamID3's number), which picks you out of an imported match.
    public long? SteamAccountId { get; set; }

    // Only the keys moved from their defaults; read them through ShortcutKeys.Gesture.
    public Dictionary<ShortcutAction, string> Shortcuts { get; set; } = [];

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
    public bool ResultsShowTiers { get; set; }
    public bool ResultsByNetWorth { get; set; } = true;
    public bool ResultsHideRarelyBuilt { get; set; }
    public bool ResultsHideDisagreed { get; set; }

    // Hero Items tab
    public string? HeroItemsHero { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<MatchMode>))]
    public MatchMode HeroItemsMode { get; set; } = MatchMode.Ranked;

    public int HeroItemsMinUsagePercent { get; set; } = 5;
    public bool HeroItemsShowChanges { get; set; }

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
