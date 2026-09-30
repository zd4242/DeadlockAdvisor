using System.IO;

namespace DeadlockAdvisor.Vision;

/// <summary>
/// Top-bar portraits cut from the API's hero cards. The API's own top-bar art is stale for some
/// heroes and doesn't exist at all for the critical and on-fire portraits, but the cards are current
/// and come in all three states, so the portraits are cut from them where the top bar crops them.
/// </summary>
public static class TopbarDerivation
{
    /// <summary>Where a hero's card for one portrait state is kept, under the top-bar folder but out of the template bank's sight.</summary>
    public static string CardFolder(string topbarDir, PortraitState state) =>
        Path.Combine(topbarDir, "_cards", state.ToString().ToLowerInvariant());
}
