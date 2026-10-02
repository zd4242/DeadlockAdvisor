using System.Reflection;

namespace DeadlockAdvisor.Core;

/// <summary>Which build this is: the release workflow stamps the version from its tag, and the SDK adds the commit.</summary>
public static class AppVersion
{
    public const string ReleasesUrl = "https://github.com/zd4242/DeadlockAdvisor/releases";

    /// <summary>"1.2.0 (15b6f95)"; "0.0.0-dev (15b6f95)" for a build made outside the release workflow.</summary>
    public static string Text { get; } =
        Describe(typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <param name="informational">"1.2.0+15b6f95dbeb7522dd8e2318fd25fffae19d64634", as the SDK writes it.</param>
    internal static string Describe(string? informational)
    {
        if (string.IsNullOrEmpty(informational))
            return "unknown";
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : $"{informational[..plus]} ({informational[(plus + 1)..][..Math.Min(7, informational.Length - plus - 1)]})";
    }
}
