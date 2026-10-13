using Avalonia.Automation.Peers;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match;
using DeadlockAdvisor.Features.Match.Board;
using DeadlockAdvisor.Features.Match.Results;
using DeadlockAdvisor.Features.Settings.Detection;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Tests.Ui;

/// <summary>What each hero has built, shown on the match bar and marked in the list; the match looks as before without a read.</summary>
public class BuildsInTheMatchTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 12, 20, 0, 0, TimeSpan.Zero);

    private static HeroItems Holding(DataStore store, BarsRead bars, params string[] itemIds) =>
        new(0, itemIds.Select((id, cell) => new CellRead(cell, CellKind.Item, ItemTemplates.CategoryOf(store.Items[id].Category), id, 0.9, 0.2, true,
            store.Items[id].Tier)).ToList(), bars, false);

    private static void ReadBuilds(UiHarness ui, DateTimeOffset at)
    {
        var store = ui.Data.Store;
        var items = store.Items.Values.Where(item => ItemTemplates.CategoryOf(item.Category) is not null).OrderBy(item => item.ItemId).ToList();
        string[] Of(string category, int tier) => items.Where(item => item.Category == category && item.Tier == tier).Select(item => item.ItemId).Take(2).ToArray();
        var match = ui.ViewModel.Match.Match;
        match.Builds.Apply("wraith", Holding(store, new BarsRead(0.2, 0.05, 0.1), [.. Of("weapon", 3), .. Of("weapon", 1), .. Of("spirit", 2)]), at);
        match.Builds.Apply("haze", Holding(store, new BarsRead(0.05, 0.05, 0.22), [.. Of("spirit", 4), .. Of("spirit", 2), .. Of("vitality", 1)]), at);
        match.Builds.Apply("dynamo", Holding(store, new BarsRead(0.0, 0.2, 0.05), Of("vitality", 4)), at);
        ui.ViewModel.Match.Board.Refresh();
        ui.ViewModel.Match.Refresh();
    }

    private static List<RosterSlotViewModel> Slots(UiHarness ui) =>
        [.. ui.ViewModel.Match.Board.AllySlots, .. ui.ViewModel.Match.Board.EnemySlots];

    [AvaloniaFact]
    public void TheMatchBarShowsABuildForEachHeroReadAndNothingForTheRest()
    {
        using var ui = new UiHarness(settings => settings.Current.ReadBuildsOnTab = true);
        MatchPageTests.SetUpMatch(ui);
        ui.Show();
        ReadBuilds(ui, DateTimeOffset.UtcNow);
        UiHarness.Settle();

        var slots = Slots(ui);
        Assert.Equal(["dynamo", "haze", "wraith"], slots.Where(slot => slot.Build is not null).Select(slot => slot.HeroId!).Order());
        Assert.All(slots.Where(slot => slot.HeroId is not null), slot => Assert.True(slot.ShowsBuilds));
        var haze = slots.Single(slot => slot.HeroId == "haze").Build!;
        Assert.True(haze.Spirit > haze.Weapon);
        Assert.False(haze.IsOld);
        Assert.Contains("Build, read just now", haze.Tip);
        Assert.True(File.Exists(ui.Screenshot("match_builds.png")));
    }

    [AvaloniaFact]
    public void ASlotsNameCarriesItsBarsNumbers()
    {
        var slot = new RosterSlot
        {
            HeroId = "ivy", HeroName = "Ivy", Team = Role.Enemy,
            Build = new BuildSummary(0.7, 0.1, 0.2, false, "tip"),
        };
        string NameOf() => ControlAutomationPeer.CreatePeerForElement(slot).GetName();

        Assert.Equal("Ivy, enemy, weapon 70%, vitality 10%, spirit 20%", NameOf());
        slot.Build = slot.Build with { IsOld = true };
        Assert.EndsWith("(read a while ago)", NameOf());
    }

    [AvaloniaFact]
    public void AMatchWithNoReadLooksAsItDid()
    {
        using var ui = new UiHarness(settings => settings.Current.ReadBuildsOnTab = true);
        MatchPageTests.SetUpMatch(ui);
        ui.Show();
        var heights = ui.Window.MatchPage.GetVisualDescendants().OfType<RosterSlot>().Select(slot => slot.Bounds.Height).Distinct().ToList();

        Assert.All(Slots(ui), slot => Assert.False(slot.ShowsBuilds));
        Assert.All(Slots(ui), slot => Assert.Null(slot.Build));

        ReadBuilds(ui, DateTimeOffset.UtcNow);
        UiHarness.Settle();
        var taller = ui.Window.MatchPage.GetVisualDescendants().OfType<RosterSlot>().Select(slot => slot.Bounds.Height).Distinct().ToList();

        Assert.Equal(heights.Single() + BuildBars.Height + 3, taller.Single());
    }

    [AvaloniaFact]
    public void WithReadingTheScoreboardOffNothingIsShownOrMarked()
    {
        using var ui = new UiHarness();
        MatchPageTests.SetUpMatch(ui);
        ui.Show();
        ReadBuilds(ui, DateTimeOffset.UtcNow);
        var results = ui.ViewModel.Match.Results;

        Assert.All(Slots(ui), slot => Assert.Null(slot.Build));
        Assert.DoesNotContain(results.Entries.OfType<ResultRowViewModel>(), row => row.IsOwned);
        Assert.False(ui.ViewModel.Match.HasOwnedItems);

        ui.Settings.Update(settings => settings.ReadBuildsOnTab = true);

        Assert.Contains(Slots(ui), slot => slot.Build is not null);
        Assert.True(ui.ViewModel.Match.HasOwnedItems);
    }

    [AvaloniaFact]
    public void ABuildFromTenMinutesAgoIsShownButMarkedOld()
    {
        using var ui = new UiHarness(settings => settings.Current.ReadBuildsOnTab = true);
        MatchPageTests.SetUpMatch(ui);
        ui.Show();

        ReadBuilds(ui, DateTimeOffset.UtcNow - BuildSummaries.OldAfter - TimeSpan.FromMinutes(2.5));

        var wraith = Slots(ui).Single(slot => slot.HeroId == "wraith").Build!;
        Assert.True(wraith.IsOld);
        Assert.Contains("12 min ago (old: the next scoreboard read refreshes it)", wraith.Tip);
        Assert.True(File.Exists(ui.Screenshot("match_builds_old.png")));
    }

    [AvaloniaFact]
    public void YourItemsAreMarkedOwnedAndTheFilterHidesThemWithoutChangingAScore()
    {
        using var ui = new UiHarness(settings => settings.Current.ReadBuildsOnTab = true);
        MatchPageTests.SetUpMatch(ui);
        ui.Show();
        var match = ui.ViewModel.Match;
        match.SelectedCutoff = MatchViewModel.CutoffPresets[0];
        Dictionary<string, string> Scores() => match.Results.Entries.OfType<ResultRowViewModel>().ToDictionary(row => row.ItemId, row => row.Score.Text);
        var before = Scores();
        var top = before.Keys.Take(2).ToList();
        ui.ViewModel.Match.Match.Builds.Apply("wraith", Holding(ui.Data.Store, new BarsRead(0.1, 0.1, 0.1), top.ToArray()), DateTimeOffset.UtcNow);

        match.Refresh();

        Assert.False(match.HideOwned);
        Assert.True(match.HasOwnedItems);
        Assert.Equal(top, match.Results.Entries.OfType<ResultRowViewModel>().Where(row => row.IsOwned).Select(row => row.ItemId));
        Assert.Equal(before, Scores());
        Assert.True(File.Exists(ui.Screenshot("match_owned.png")));

        match.HideOwned = true;

        var after = Scores();
        Assert.Empty(after.Keys.Intersect(top));
        Assert.All(after, pair => Assert.Equal(before[pair.Key], pair.Value));
        Assert.True(ui.Settings.Current.ResultsHideOwned);
        Assert.True(match.HasActiveFilters);
        Assert.EndsWith("• Items you already own are hidden", match.FiltersButtonTip);
        Assert.True(File.Exists(ui.Screenshot("match_owned_hidden.png")));

        match.HideOwned = false;
        Assert.Equal(before, Scores());
    }

    [AvaloniaFact]
    public void TheExplanationOfAnOwnedItemSaysSo()
    {
        using var ui = new UiHarness(settings => settings.Current.ReadBuildsOnTab = true);
        MatchPageTests.SetUpMatch(ui);
        var match = ui.ViewModel.Match;
        match.SelectedCutoff = MatchViewModel.CutoffPresets[0];
        var first = match.Results.Entries.OfType<ResultRowViewModel>().First().ItemId;
        match.Match.Builds.Apply("wraith", Holding(ui.Data.Store, new BarsRead(0.1, 0.1, 0.1), first), DateTimeOffset.UtcNow);
        match.Refresh();

        match.Results.Select(match.Results.Entries.OfType<ResultRowViewModel>().First());
        Assert.True(match.Explain.IsOwned);

        match.Results.Select(match.Results.Entries.OfType<ResultRowViewModel>().Skip(3).First());
        Assert.False(match.Explain.IsOwned);
    }

    [AvaloniaFact]
    public void BuildsAreSavedWithTheMatchAndAReopenedAppShowsThemAgain()
    {
        using var ui = new UiHarness(settings => settings.Current.ReadBuildsOnTab = true);
        MatchPageTests.SetUpMatch(ui);
        ReadBuilds(ui, DateTimeOffset.UtcNow);

        // The match page saves the match on each change; a change is what the scoreboard read raises.
        ui.ViewModel.Match.MatchUpdated();
        var saved = ui.Settings.Current.LastMatch!;
        Assert.Equal(["dynamo", "haze", "wraith"], saved.Builds.Keys.Order());

        using var reopened = new UiHarness(settings =>
        {
            settings.Current.ReadBuildsOnTab = true;
            settings.Current.LastMatch = saved;
        });
        Assert.NotNull(reopened.ViewModel.Match.Match.BuildOf("haze"));
        Assert.Contains(Slots(reopened), slot => slot.HeroId == "haze" && slot.Build is not null);
    }

    [AvaloniaFact]
    public void SettingsSayWhenTheLastReadWas()
    {
        using var ui = new UiHarness(settings => settings.Current.ReadBuildsOnTab = true);
        var detection = ui.ViewModel.Settings.Detection;
        Assert.Equal(DetectionSettingsViewModel.DescribeLastRead(null), detection.LastReadDescription);
        Assert.Contains("Nothing read yet", detection.LastReadDescription);

        MatchPageTests.SetUpMatch(ui);
        var at = new DateTimeOffset(2026, 10, 12, 14, 32, 0, TimeSpan.Zero);
        ReadBuilds(ui, at);
        ui.ViewModel.Match.MatchUpdated();

        Assert.Contains($"{at.ToLocalTime():t}", detection.LastReadDescription);
        Assert.DoesNotContain("Nothing read yet", detection.LastReadDescription);
    }
}
