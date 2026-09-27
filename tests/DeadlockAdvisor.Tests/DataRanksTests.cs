using DeadlockAdvisor.Features.Match;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

/// <summary>The Match page's rank filter over a replayed download: each change reworks the match data on the spot.</summary>
public sealed class DataRanksTests : IDisposable
{
    private readonly DataFixture _fixture = new();
    private readonly MatchStatsService _matchStats = new(new FakeDeadlockApi());
    private int _replaced;

    public DataRanksTests()
    {
        _fixture.Data.StoreReplaced.Subscribe(_ => _replaced++);
    }

    public void Dispose() => _fixture.Dispose();

    private DataRanksViewModel Filter() => new(_fixture.Data, _matchStats, new NotificationService(new FakeLoggingService()));

    private async Task DownloadAsync()
    {
        _matchStats.Apply(_fixture.Data.Store, await MatchStatsServiceTests.ReplayedCountsAsync());
        _fixture.Data.NotifyReplaced();
        _replaced = 0;
    }

    private RankRange? SavedRange() => MatchStatsMath.RankOf(_fixture.Saved().MatchMeta);

    [Fact]
    public void DataFromBeforeRankSplitsCantBeFiltered()
    {
        using var filter = Filter();

        Assert.False(filter.CanFilter);
        Assert.Empty(filter.Ranks);
        Assert.True(filter.EveryMatch);
        Assert.Equal("Data: every match", filter.Label);
        Assert.Equal("Enemies: 5928 lifts, reliability 0.71\nYour hero: 4421 lifts, reliability 0.93", filter.Status);
    }

    [Fact]
    public async Task EachChangeReworksTheMatchDataAndSavesIt()
    {
        await DownloadAsync();
        using var filter = Filter();
        Assert.True(filter.CanFilter);
        Assert.Equal(("Initiate", "Ascendant"), (filter.From!.Name, filter.To!.Name));

        filter.RankedOnly = true;
        Assert.Equal(new RankRange(1, 10), SavedRange());
        Assert.Equal("Data: every ranked match", filter.Label);
        Assert.False(filter.EveryMatch);
        Assert.Equal(1, _replaced);

        filter.From = filter.Ranks[4];
        Assert.Equal(new RankRange(5, 10), SavedRange());
        Assert.Equal("Data: Mystic+", filter.Label);
        Assert.Contains("Ranked matches only: Mystic+.", MatchStatsMath.DataNote(_fixture.Data.Store.MatchMeta, 0));

        // Below the start: the start follows it down, in a single rework.
        filter.To = filter.Ranks[2];
        Assert.Equal(new RankRange(3, 3), SavedRange());
        Assert.Equal(("Acolyte", "Acolyte"), (filter.From.Name, filter.To.Name));
        Assert.Equal(3, _replaced);

        filter.EveryMatch = true;
        Assert.Null(SavedRange());
        Assert.Equal("Data: every match", filter.Label);
        Assert.Equal(4, _replaced);
    }

    [Fact]
    public async Task TheFilterPicksUpTheRangeTheDataWasLeftAt()
    {
        await DownloadAsync();
        _matchStats.Refilter(_fixture.Data.Store, new RankRange(5, 8));
        _fixture.Data.Reload();

        using var filter = Filter();

        Assert.True(filter.RankedOnly);
        Assert.Equal(("Mystic", "Oracle"), (filter.From!.Name, filter.To!.Name));
        Assert.Equal("Data: Mystic – Oracle", filter.Label);
        // Only the reload: showing the range doesn't rework it.
        Assert.Equal(1, _replaced);
    }
}
