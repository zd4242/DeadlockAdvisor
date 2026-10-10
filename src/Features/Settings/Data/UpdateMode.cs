using DeadlockAdvisor.Models;

namespace DeadlockAdvisor.Features.Settings.Data;

/// <summary>How one thing keeps up to date: on its own, by saying when there's something, or not at all.</summary>
public enum UpdateMode
{
    Automatic,
    TellMe,
    Off,
}

/// <summary>One choice in a mode selector, with what it does for that source.</summary>
public sealed record UpdateModeOption(UpdateMode Mode, string Label, string Description);

/// <summary>
/// The modes as a view over the settings flags, which stay as they are so a <c>settings.json</c> reads the same in an
/// older version: <c>Automatic</c> is the update flag, <c>TellMe</c> the check-only flag with the update flag off.
/// Choosing <c>Automatic</c> leaves the check-only flag as it was, since it only matters with the update flag off.
/// </summary>
public static class UpdateModes
{
    public static UpdateMode MatchData(AppSettings settings) => Read(settings.AutoUpdateMatchData, settings.CheckForNewerPatch);

    public static void SetMatchData(AppSettings settings, UpdateMode mode) =>
        (settings.AutoUpdateMatchData, settings.CheckForNewerPatch) = Write(mode, settings.CheckForNewerPatch);

    public static UpdateMode Formulas(AppSettings settings) => Read(settings.AutoUpdateModel, settings.CheckForNewHeroes);

    public static void SetFormulas(AppSettings settings, UpdateMode mode) =>
        (settings.AutoUpdateModel, settings.CheckForNewHeroes) = Write(mode, settings.CheckForNewHeroes);

    /// <summary>The app is only ever checked for, never installed on its own: <c>Automatic</c> reads as <c>TellMe</c>.</summary>
    public static UpdateMode App(AppSettings settings) => settings.CheckForAppUpdates ? UpdateMode.TellMe : UpdateMode.Off;

    public static void SetApp(AppSettings settings, UpdateMode mode) => settings.CheckForAppUpdates = mode != UpdateMode.Off;

    private static UpdateMode Read(bool automatic, bool tellMe) =>
        automatic ? UpdateMode.Automatic : tellMe ? UpdateMode.TellMe : UpdateMode.Off;

    private static (bool Automatic, bool TellMe) Write(UpdateMode mode, bool tellMe) => mode switch
    {
        UpdateMode.Automatic => (true, tellMe),
        UpdateMode.TellMe => (false, true),
        _ => (false, false),
    };
}
