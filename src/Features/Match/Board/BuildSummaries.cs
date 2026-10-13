using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Features.Match.Board;

/// <summary>What the match bar says of a hero's build: the bars and the tooltip behind them.</summary>
public static class BuildSummaries
{
    /// <summary>A read this old is still shown but drawn faded: the next press of the scoreboard key refreshes it.</summary>
    public static readonly TimeSpan OldAfter = TimeSpan.FromMinutes(10);

    public static BuildSummary Of(HeroBuild build, DataStore store, DateTimeOffset now)
    {
        var shares = build.Shares();
        var age = now - build.At;
        var bars = new BuildSummary(shares[ShopCategory.Weapon], shares[ShopCategory.Vitality], shares[ShopCategory.Spirit], age >= OldAfter, "");
        return bars with { Tip = Tip(build, store, bars, age) };
    }

    private static string Tip(HeroBuild build, DataStore store, BuildSummary bars, TimeSpan age)
    {
        var lines = new List<string> { $"Build, read {AgeText(age)}{(bars.IsOld ? " (old: the next scoreboard read refreshes it)" : "")}" };
        var estimate = Enum.GetValues<ShopCategory>().Any(category => build.InvestmentIn(category).IsEstimate);
        lines.Add($"Souls in {bars.Described}{(estimate ? " (about: some items weren't seen)" : "")}");

        var names = build.Cells.Where(cell => cell.IsIdentified).Select(cell => store.Items.TryGetValue(cell.ItemId!, out var item) ? item.ItemName : cell.ItemId!).ToList();
        if (names.Count > 0)
            lines.Add($"Items: {string.Join(", ", names)}");

        var unknown = build.Cells.Where(cell => !cell.IsIdentified).Select(Describe).ToList();
        if (unknown.Count > 0)
            lines.Add($"Not recognised: {string.Join(", ", unknown)}");
        return string.Join("\n", lines);
    }

    private static string Describe(BuildCell cell) => cell.Kind switch
    {
        CellKind.Hidden => "a hidden pick",
        CellKind.Unseen => "a covered cell",
        _ => $"a {cell.Category!.Value.ToString().ToLowerInvariant()} item{(cell.Tier is { } tier ? $" (tier {tier})" : "")}",
    };

    private static string AgeText(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes} min ago";
        return $"{(int)age.TotalHours}h ago";
    }
}
