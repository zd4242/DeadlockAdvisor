using Avalonia.Headless.XUnit;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match;
using DeadlockAdvisor.Features.Match.Detect;
using DeadlockAdvisor.Features.Match.Import;
using DeadlockAdvisor.Features.Match.Results;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Tests.Ui;

namespace DeadlockAdvisor.Tests;

/// <summary>The Match page rescores for an edit to the formulas only while it is on screen.</summary>
public sealed class MatchRescoringTests : IDisposable
{
    private readonly DataFixture _fixture = new();
    private readonly MatchViewModel _page;

    public MatchRescoringTests()
    {
        var log = new FakeLoggingService();
        var detect = new DetectAction(_fixture.Data, _fixture.Settings, _fixture.Modals, new NotificationService(log), new FakeScreenCapture(), log, new FakeConnectivity());
        var import = new ImportMatchAction(_fixture.Data, _fixture.Settings, _fixture.Modals, new MatchLookupService(new FakeDeadlockApi()), log);
        var dataRanks = new DataRanksViewModel(_fixture.Data, new MatchStatsService(new FakeDeadlockApi()), new NotificationService(log));
        _page = new MatchViewModel(_fixture.Data, _fixture.Settings, detect, import, dataRanks);
        _page.Board.SetRole("haze", Role.Enemy);
        _page.Board.SetRole("wraith", Role.Self);
    }

    public void Dispose()
    {
        _page.Dispose();
        _fixture.Dispose();
    }

    /// <summary>What the list shows now: every listed item with its score.</summary>
    private static string Listed(MatchViewModel page) =>
        string.Join(";", page.Results.Entries.OfType<ResultRowViewModel>().Select(row => $"{row.ItemId}={row.Score.Shown}"));

    /// <summary>Rates the enemy Haze on every trait, which moves every score.</summary>
    private static void RateHaze(IDataService data, double score)
    {
        foreach (var category in data.Store.Categories.Keys)
            data.Store.SetHeroScore("haze", category, score);
    }

    private void EditFormulas(double score)
    {
        RateHaze(_fixture.Data, score);
        _fixture.Data.MarkEdited(DataFiles.HeroScores);
        _fixture.Clock.AdvanceBy(DataService.RescoreThrottle);
    }

    [Fact]
    public void ARescoreWhileShownRefreshesTheList()
    {
        var before = Listed(_page);
        Assert.NotEqual("", before);

        EditFormulas(10);

        Assert.NotEqual(before, Listed(_page));
    }

    [Fact]
    public void ARescoreWhileHiddenWaitsAndTheListCatchesUpWhenItShowsAgain()
    {
        var before = Listed(_page);
        _page.SetShown(false);

        EditFormulas(10);
        EditFormulas(9);

        Assert.Equal(before, Listed(_page));

        _page.SetShown(true);
        var caughtUp = Listed(_page);
        Assert.NotEqual(before, caughtUp);

        // Nothing was edited since, so showing it again leaves it as it is.
        _page.SetShown(false);
        _page.SetShown(true);
        Assert.Equal(caughtUp, Listed(_page));
    }

    [Fact]
    public void AReplacedStoreRebindsEvenWhileHidden()
    {
        var before = Listed(_page);
        _page.SetShown(false);

        RateHaze(_fixture.Data, 10);
        _fixture.Data.NotifyReplaced();

        Assert.NotEqual(before, Listed(_page));
    }

    [Fact]
    public void AChangedMatchRefreshesEvenWhileHidden()
    {
        var before = Listed(_page);
        _page.SetShown(false);

        _page.Board.SetRole("abrams", Role.Enemy);

        Assert.NotEqual(before, Listed(_page));
    }

    [AvaloniaFact]
    public async Task TheMainWindowShowsTheMatchPageOnlyWhileItsTabIsOpen()
    {
        using var ui = new UiHarness();
        ui.ViewModel.Match.Board.SetRole("haze", Role.Enemy);
        ui.ViewModel.Match.Board.SetRole("wraith", Role.Self);
        ui.Show();
        var match = ui.ViewModel.Match;

        async Task<string> ListedAfterEditingAsync(double score)
        {
            RateHaze(ui.Data, score);
            ui.Data.MarkEdited(DataFiles.HeroScores);
            await Task.Delay(DataService.RescoreThrottle * 3);
            UiHarness.Settle();
            return Listed(match);
        }

        var before = Listed(match);
        ui.ViewModel.CurrentPage = 1;
        Assert.Equal(before, await ListedAfterEditingAsync(10));
        ui.ViewModel.CurrentPage = 0;
        var shown = Listed(match);
        Assert.NotEqual(before, shown);

        // Settings covers the page, so it counts as hidden.
        ui.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        Assert.Equal(shown, await ListedAfterEditingAsync(9));
        ui.ViewModel.Settings.CloseCommand.Execute().Subscribe();
        Assert.NotEqual(shown, Listed(match));
    }
}
