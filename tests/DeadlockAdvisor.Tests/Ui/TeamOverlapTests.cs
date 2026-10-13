using Avalonia.Headless.XUnit;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match;
using DeadlockAdvisor.Features.Match.Results;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Tests.Ui;

/// <summary>An effect your team already has counts for less in the list, says so in the explanation, and is told once when two teammates repeat it.</summary>
public class TeamOverlapTests
{
    private const string Lower = "mystic_vulnerability";
    private const string Upper = "escalating_exposure";

    private static HeroItems Holding(DataStore store, params string[] itemIds) =>
        new(0, itemIds.Select((id, cell) => new CellRead(cell, CellKind.Item, ItemTemplates.CategoryOf(store.Items[id].Category), id, 0.9, 0.2, true,
            store.Items[id].Tier)).ToList(), new BarsRead(0.1, 0.1, 0.1), false);

    private static void Hold(UiHarness ui, string heroId, params string[] itemIds) =>
        ui.ViewModel.Match.Match.Builds.Apply(heroId, Holding(ui.Data.Store, itemIds), DateTimeOffset.UtcNow);

    private static UiHarness Match()
    {
        var ui = new UiHarness(settings => settings.Current.ReadBuildsOnTab = true);
        MatchPageTests.SetUpMatch(ui);
        ui.ViewModel.Match.SelectedCutoff = MatchViewModel.CutoffPresets[0];
        ui.ViewModel.Match.SelectedRank = MatchViewModel.RankPresets[1];
        return ui;
    }

    private static Dictionary<string, double> Scores(UiHarness ui) =>
        ItemScoring.ScoreAll(ui.Data.Store, ui.Data.Matrix, ui.ViewModel.Match.Match, NetWorthWeights.For(ui.ViewModel.Match.Match)).ToDictionary(item => item.ItemId, item => item.Score);

    [AvaloniaFact]
    public void ATeammatesItemTakesItsEffectOffTheOnesOfItsBuildAndNothingElse()
    {
        using var ui = Match();
        var match = ui.ViewModel.Match;
        var store = ui.Data.Store;
        var before = Scores(ui);
        Assert.False(match.HasBuilds);
        Assert.True(match.ByOverlap);

        Hold(ui, "dynamo", Lower);
        match.Refresh();

        Assert.True(match.HasBuilds);
        var rows = match.Results.Entries.OfType<ResultRowViewModel>().ToDictionary(row => row.ItemId);
        var covered = ItemScoring.ScoreAll(store, ui.Data.Matrix, match.Match, NetWorthWeights.For(match.Match), TeamOverlap.For(store, match.Match))
            .ToDictionary(item => item.ItemId, item => item.Score);
        Assert.NotEqual(before[Lower], covered[Lower]);
        Assert.NotEqual(before[Upper], covered[Upper]);
        Assert.All(before.Where(pair => !store.ItemEffects.ContainsKey(pair.Key)), pair => Assert.Equal(pair.Value, covered[pair.Key]));
        Assert.Equal(covered[Upper], rows[Upper].Score.Value, 6);

        match.ByOverlap = false;

        Assert.False(ui.Settings.Current.ResultsByOverlap);
        Assert.Equal(before[Upper], match.Results.Entries.OfType<ResultRowViewModel>().Single(row => row.ItemId == Upper).Score.Value, 6);
        match.ByOverlap = true;
        Assert.True(ui.Settings.Current.ResultsByOverlap);
    }

    [AvaloniaFact]
    public void WithReadingTheScoreboardOffNothingIsDiscounted()
    {
        using var ui = new UiHarness();
        MatchPageTests.SetUpMatch(ui);
        var match = ui.ViewModel.Match;
        match.SelectedCutoff = MatchViewModel.CutoffPresets[0];
        match.SelectedRank = MatchViewModel.RankPresets[1];
        var before = match.Results.Entries.OfType<ResultRowViewModel>().ToDictionary(row => row.ItemId, row => row.Score.Value);

        Hold(ui, "dynamo", Lower);
        match.Refresh();

        Assert.False(match.HasBuilds);
        Assert.Equal(before, match.Results.Entries.OfType<ResultRowViewModel>().ToDictionary(row => row.ItemId, row => row.Score.Value));
    }

    [AvaloniaFact]
    public void TheExplanationSaysWhatYourTeamAlreadyHas()
    {
        using var ui = Match();
        var match = ui.ViewModel.Match;
        ui.Show();
        Hold(ui, "dynamo", Lower);
        match.Refresh();

        match.Results.Select(match.Results.Entries.OfType<ResultRowViewModel>().Single(row => row.ItemId == Upper));

        Assert.Equal("Dynamo holds Mystic Vulnerability, which covers 100% of this item's Spirit Resist reduction.\n"
                     + "That part of its score counts for the rest; its other stats count in full.", match.Explain.OverlapNote);
        Assert.True(File.Exists(ui.Screenshot("match_overlap_explain.png")));

        match.ByOverlap = false;
        Assert.Null(match.Explain.OverlapNote);
    }

    [AvaloniaFact]
    public void TwoTeammatesRepeatingAnEffectAreToldOncePerOverlapWithAChimeOnlyWhileTheGameHasTheScreen()
    {
        using var ui = Match();
        var match = ui.ViewModel.Match;
        ui.Foreground.IsAnotherAppInFront = true;
        var toasts = ui.Services.GetService(typeof(INotificationService)) as INotificationService;
        var recent = toasts!.Recent.Count;

        Hold(ui, "dynamo", Lower);
        match.Refresh();
        Assert.Equal(recent, toasts.Recent.Count);

        Hold(ui, "kelvin", Upper);
        match.Refresh();
        match.Refresh();

        var told = toasts.Recent.Skip(recent).ToList();
        var toast = Assert.Single(told);
        Assert.Equal("Dynamo and Kelvin hold Mystic Vulnerability and Escalating Exposure: they share one Spirit Resist reduction, and only the stronger counts.",
            toast.Message);
        Assert.Equal([AttentionKind.NeedsLook], ui.Attention.Chimes);
        Assert.Equal(1, ui.Attention.Flashes);

        // The same two holding the same items isn't news again; a third doing so is.
        ui.ViewModel.Match.MatchUpdated();
        Assert.Single(toasts.Recent.Skip(recent));
        Hold(ui, "paradox", Lower);
        match.Refresh();
        Assert.Equal(2, toasts.Recent.Skip(recent).Count());
    }

    [AvaloniaFact]
    public void NoChimeWhenTheAdvisorIsInFrontOrTheSoundIsOff()
    {
        using var ui = Match();
        var match = ui.ViewModel.Match;

        Hold(ui, "dynamo", Lower);
        Hold(ui, "kelvin", Upper);
        match.Refresh();

        Assert.Empty(ui.Attention.Chimes);
        Hold(ui, "paradox", Lower);
        ui.Foreground.IsAnotherAppInFront = true;
        ui.Settings.Update(settings => settings.SoundOnDetect = false);
        match.Refresh();
        Assert.Empty(ui.Attention.Chimes);
        Assert.Equal(0, ui.Attention.Flashes);
    }
}
