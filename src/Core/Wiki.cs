namespace DeadlockAdvisor.Core;

/// <summary>The community Deadlock wiki, whose hero and item pages are titled with their in-game names.</summary>
public static class Wiki
{
    private const string _root = "https://deadlock.wiki/";

    /// <summary>MediaWiki titles take underscores for spaces; anything else awkward (&amp;, ') is percent-encoded.</summary>
    public static Uri PageUrl(string title) => new(_root + Uri.EscapeDataString(title.Trim().Replace(' ', '_')));
}
