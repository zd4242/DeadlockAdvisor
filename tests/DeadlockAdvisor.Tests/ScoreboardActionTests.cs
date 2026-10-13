using System.Reactive.Concurrency;
using DeadlockAdvisor.Features.Match.Scoreboard;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Vision;
using DeadlockAdvisor.Vision.Items;
using static DeadlockAdvisor.Tests.Support.VisionData;

namespace DeadlockAdvisor.Tests;

/// <summary>The quiet read of the scoreboard while its key is held: what it does to the match, and the many times it does nothing at all.</summary>
public sealed class ScoreboardActionTests : IDisposable
{
    // screen_2560x1440_band_2.json: the heroes of the capture below, you in slot 0, and what its pills read.
    private static readonly string[] _heroes =
        ["apollo", "ivy", "wraith", "bebop", "silver", "doorman", "celeste", "lash", "venator", "paige", "mirage", "mo_and_krill"];

    private static readonly int[] _souls = [16_000, 13_000, 20_000, 12_000, 18_000, 12_000, 17_000, 13_000, 14_000, 15_000, 19_000, 15_000];

    private readonly DataFixture _fixture = new();
    private readonly FakeScreenCapture _capture = new();
    private readonly FakeScoreboardKey _key = new();
    private readonly HistoricalScheduler _clock = new();
    private readonly List<object> _shown = [];
    private readonly IDisposable _watchModals;
    private readonly ScoreboardAction _action;
    private int _changed;

    public ScoreboardActionTests()
    {
        _fixture.Settings.Current.ReadBuildsOnTab = true;
        _watchModals = _fixture.Modals.ShowModalObservable.Subscribe(_shown.Add);
        _action = new ScoreboardAction(_key, _capture, _fixture.Settings, _fixture.Data, _fixture.Modals, new FakeLoggingService(), _clock);
    }

    public void Dispose()
    {
        _watchModals.Dispose();
        _fixture.Dispose();
    }

    private static MatchState Applied(IReadOnlyList<string> heroes)
    {
        var match = new MatchState();
        VisionApply.ApplyToMatch(match, heroes, 0);
        return match;
    }

    private void WithArt() => CopyTopbarInto(_fixture.Data.AssetsDir);

    /// <summary>Press and let the virtual clock run until the read has ended, as the real one would in about a second.</summary>
    private async Task<ScoreboardOutcome> PressAsync(MatchState match)
    {
        var task = _action.OnPressedAsync(match, () => _changed++);
        for (var step = 0; step < 4000 && !task.IsCompleted; step++)
        {
            _clock.AdvanceBy(TimeSpan.FromMilliseconds(50));
            await Task.Delay(1);
        }
        return await task;
    }

    private static ScreenCapture Blank()
    {
        var band = Capture().Band;
        return new ScreenCapture(new RgbImage(band.Width, band.Height), 2560, 1440);
    }

    [Fact]
    public async Task TheSameMatchGetsANewNetWorthReadingAndNothingIsShown()
    {
        WithArt();
        _capture.Next = Capture();
        var match = Applied(_heroes);

        var outcome = await PressAsync(match);

        Assert.Equal(ScoreboardOutcome.Read, outcome);
        Assert.Equal(_souls, _heroes.Select(hero => match.NetWorth.Latest(hero) ?? 0));
        Assert.Equal(1, _changed);
        Assert.Contains("2560x1440", _fixture.Settings.Current.VisionGeometry.Keys);
        Assert.Equal([ScoreboardAction.CaptureFraction], _capture.Tall);
        Assert.Null(_capture.Minimized);
        Assert.Empty(_shown);
        Assert.False(_fixture.Modals.IsModalOpen);
    }

    [Fact]
    public async Task HeroesReorderedOnTheMatchBarStillGetTheirOwnNetWorth()
    {
        WithArt();
        _capture.Next = Capture();
        var match = Applied(_heroes);
        match.MoveOnto("wraith", "apollo");
        match.MoveOnto("mo_and_krill", "celeste");
        match.MoveOnto("lash", "venator");
        Assert.Equal(["wraith", "apollo", "ivy", "bebop", "silver", "doorman"], match.OwnTeam);

        Assert.Equal(ScoreboardOutcome.Read, await PressAsync(match));

        Assert.Equal(_souls, _heroes.Select(hero => match.NetWorth.Latest(hero) ?? 0));
        Assert.Equal(Enumerable.Range(0, 12), _heroes.Select(hero => match.Slots[hero]));
    }

    private string[] KeptCaptures() =>
        Directory.Exists(Path.Combine(_fixture.Data.DataRoot, "captures"))
            ? Directory.GetFiles(Path.Combine(_fixture.Data.DataRoot, "captures"), "scoreboard_*.png")
            : [];

    [Fact]
    public async Task TheBoardIsKeptWithWhatWasReadOnlyWhenTheSettingIsOn()
    {
        WithArt();
        _capture.Next = Capture();

        await PressAsync(Applied(_heroes));
        Assert.Empty(KeptCaptures());

        _fixture.Settings.Update(s => s.KeepScoreboardCaptures = true);
        Assert.Equal(ScoreboardOutcome.Read, await PressAsync(Applied(_heroes)));

        var image = Assert.Single(KeptCaptures());
        var note = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(image, ".json")))!;
        Assert.Equal(2560, (int)note["screen_width"]!);
        Assert.Equal(_heroes, note["heroes"]!.AsArray().Select(hero => (string?)hero));
        Assert.NotNull(Geometry.FromJson(note["grid"]!.AsObject()));
        Assert.Equal(Capture().Band.Width, ImageFile.Load(image).Width);
    }

    [Fact]
    public async Task BoardsAreKeptNoMoreOftenThanEveryTenSeconds()
    {
        WithArt();
        _fixture.Settings.Current.KeepScoreboardCaptures = true;
        _capture.Next = Capture();
        var match = Applied(_heroes);

        await PressAsync(match);
        await PressAsync(match);
        Assert.Single(KeptCaptures());

        // The net worth is still refreshed by the press that kept nothing.
        Assert.Equal(2, _changed);

        _clock.AdvanceBy(ScoreboardAction.KeepEvery);
        await PressAsync(match);
        Assert.Equal(2, KeptCaptures().Length);
    }

    [Fact]
    public async Task ABoardThatIsntTheMatchesIsNotKept()
    {
        WithArt();
        _fixture.Settings.Current.KeepScoreboardCaptures = true;
        _capture.Next = Capture();

        await PressAsync(Applied([.. _heroes.Reverse()]));
        await PressAsync(new MatchState());

        Assert.Empty(KeptCaptures());
    }

    [Fact]
    public async Task ItWaitsForTheBoardToFadeInBeforeTakingTheCapture()
    {
        WithArt();
        _capture.Next = Capture();
        var task = _action.OnPressedAsync(Applied(_heroes), () => _changed++);

        _clock.AdvanceBy(ScoreboardAction.SettleDelay - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, _capture.Captures);

        _clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        for (var step = 0; step < 4000 && !task.IsCompleted; step++)
        {
            _clock.AdvanceBy(TimeSpan.FromMilliseconds(50));
            await Task.Delay(1);
        }
        Assert.Equal(ScoreboardOutcome.Read, await task);
        Assert.Equal(1, _capture.Captures);
    }

    [Fact]
    public async Task ABoardOfAnotherMatchIsIgnored()
    {
        WithArt();
        _capture.Next = Capture();
        var match = Applied([.. _heroes.Reverse()]);

        var outcome = await PressAsync(match);

        Assert.Equal(ScoreboardOutcome.DifferentMatch, outcome);
        Assert.True(match.NetWorth.IsEmpty);
        Assert.Equal(0, _changed);
        Assert.Empty(_shown);
    }

    [Fact]
    public async Task WithNoMatchAppliedItDoesNotEvenTakeACapture()
    {
        WithArt();
        _capture.Next = Capture();

        var outcome = await PressAsync(new MatchState());

        Assert.Equal(ScoreboardOutcome.NoMatch, outcome);
        Assert.Equal(0, _capture.Captures);
    }

    [Fact]
    public async Task WithTheSettingOffItDoesNothing()
    {
        WithArt();
        _capture.Next = Capture();
        _fixture.Settings.Update(s => s.ReadBuildsOnTab = false);

        var outcome = await PressAsync(Applied(_heroes));

        Assert.Equal(ScoreboardOutcome.Off, outcome);
        Assert.Equal(0, _capture.Captures);
    }

    [Fact]
    public async Task WhileADialogIsOpenItDoesNothing()
    {
        WithArt();
        _capture.Next = Capture();
        _fixture.Modals.ShowMessage("Anything", "Open.");

        var outcome = await PressAsync(Applied(_heroes));

        Assert.Equal(ScoreboardOutcome.ModalOpen, outcome);
        Assert.Equal(0, _capture.Captures);
        Assert.Single(_shown);
    }

    [Fact]
    public async Task AKeyLetGoBeforeTheBoardIsUpIsNotRead()
    {
        WithArt();
        _capture.Next = Capture();
        _key.IsHeld = false;

        var outcome = await PressAsync(Applied(_heroes));

        Assert.Equal(ScoreboardOutcome.Released, outcome);
        Assert.Equal(0, _capture.Captures);
    }

    [Fact]
    public async Task ABoardNotFoundIsLookedForOnceMoreAndStopsThere()
    {
        WithArt();
        _capture.Next = Blank();

        var outcome = await PressAsync(Applied(_heroes));

        Assert.Equal(ScoreboardOutcome.NotFound, outcome);
        Assert.Equal(2, _capture.Captures);
        Assert.Empty(_shown);
    }

    [Fact]
    public async Task TheSecondLookCanFindWhatTheFirstMissed()
    {
        WithArt();
        _capture.Sequence.Enqueue(Blank());
        _capture.Next = Capture();
        var match = Applied(_heroes);

        var outcome = await PressAsync(match);

        Assert.Equal(ScoreboardOutcome.Read, outcome);
        Assert.Equal(2, _capture.Captures);
        Assert.Equal(16_000, match.NetWorth.Latest("apollo"));
    }

    [Fact]
    public async Task AKeyLetGoBeforeTheSecondLookEndsIt()
    {
        WithArt();
        _capture.Next = Blank();
        _capture.Captured = () => _key.IsHeld = false;

        var outcome = await PressAsync(Applied(_heroes));

        Assert.Equal(ScoreboardOutcome.Released, outcome);
        Assert.Equal(1, _capture.Captures);
    }

    [Fact]
    public async Task APressWhileOneIsBeingReadIsDropped()
    {
        WithArt();
        _capture.Next = Capture();
        var match = Applied(_heroes);
        var first = _action.OnPressedAsync(match, () => _changed++);

        Assert.Equal(ScoreboardOutcome.Busy, await _action.OnPressedAsync(match, () => _changed++));

        for (var step = 0; step < 4000 && !first.IsCompleted; step++)
        {
            _clock.AdvanceBy(TimeSpan.FromMilliseconds(50));
            await Task.Delay(1);
        }
        Assert.Equal(ScoreboardOutcome.Read, await first);
        Assert.Equal(1, _capture.Captures);
    }

    [Fact]
    public async Task ACaptureThatFailsIsLoggedNotShown()
    {
        WithArt();
        _capture.Next = null;

        var outcome = await PressAsync(Applied(_heroes));

        Assert.Equal(ScoreboardOutcome.CaptureFailed, outcome);
        Assert.Empty(_shown);
    }

    [Fact]
    public async Task WithoutHeroArtThereIsNothingToReadWith()
    {
        _capture.Next = Capture();

        var outcome = await PressAsync(Applied(_heroes));

        Assert.Equal(ScoreboardOutcome.NoArt, outcome);
        Assert.Empty(_shown);
    }

    private static ItemCorpusBoard Board(string name) => ItemCorpusBoard.LoadAll(ItemData.CorpusDir).Single(board => board.Name == name);

    /// <summary>The items a label names, by hero, as the board has them.</summary>
    private static Dictionary<string, string[]> Named(ItemCorpusBoard board) =>
        _heroes.Select((hero, slot) => (hero, items: board.Label.HeroAt(slot).Cells
                .Select(cell => HeroLabel.Parse(cell).ItemId).OfType<string>().Order().ToArray()))
            .ToDictionary(pair => pair.hero, pair => pair.items);

    [Fact]
    public async Task EachHerosItemsAndBarsAreKeptInTheMatchAlongsideTheirNetWorth()
    {
        WithArt();
        ItemData.CopyArtInto(_fixture.Data.AssetsDir);
        var board = Board("late_clean");
        _capture.Next = ItemData.ScoreboardOf(board);
        var match = Applied(_heroes);

        var outcome = await PressAsync(match);

        Assert.Equal(ScoreboardOutcome.Read, outcome);
        Assert.Equal(1, _changed);
        Assert.Equal(_souls, _heroes.Select(hero => match.NetWorth.Latest(hero) ?? 0));
        // A slot whose panel reads as covered (the fixture's strip is a screenshot with a flat panel over some bars) says nothing, so it has no build.
        var built = Named(board).Where(pair => match.BuildOf(pair.Key) is not null).ToList();
        Assert.True(built.Count >= 9, $"builds for {built.Count} of 12 heroes");
        foreach (var (hero, named) in built)
        {
            var build = match.BuildOf(hero)!;
            Assert.NotNull(build.Bars);
            Assert.All(build.ItemIds, id => Assert.Contains(id, named));
            Assert.True(build.ItemIds.Count() >= named.Length - 2, $"{hero}: {build.ItemIds.Count()} of {named.Length}");
        }
        Assert.Equal(built.Count, match.Builds.All.Count);
    }

    [Fact]
    public async Task ASecondReadAddsToWhatTheFirstShowed()
    {
        WithArt();
        ItemData.CopyArtInto(_fixture.Data.AssetsDir);
        _capture.Next = ItemData.ScoreboardOf(Board("late_deep"));
        var match = Applied(_heroes);
        await PressAsync(match);
        var first = match.BuildOf("apollo")!;

        _clock.AdvanceBy(TimeSpan.FromMinutes(1));
        await PressAsync(match);

        var second = match.BuildOf("apollo")!;
        Assert.NotSame(first, second);
        Assert.Equal(first.ItemIds.Order(), second.ItemIds.Order());
        Assert.Equal(2, _changed);
    }

    [Fact]
    public async Task WithoutItemArtTheNetWorthIsStillReadAndNoBuildIsKept()
    {
        WithArt();
        _capture.Next = ItemData.ScoreboardOf(Board("late_clean"));
        var match = Applied(_heroes);

        var outcome = await PressAsync(match);

        Assert.Equal(ScoreboardOutcome.Read, outcome);
        Assert.Equal(16_000, match.NetWorth.Latest("apollo"));
        Assert.True(match.Builds.IsEmpty);
    }

    [Fact]
    public async Task ABoardOfAnotherMatchAddsNoBuilds()
    {
        WithArt();
        ItemData.CopyArtInto(_fixture.Data.AssetsDir);
        _capture.Next = ItemData.ScoreboardOf(Board("late_clean"));
        var match = Applied([.. _heroes.Reverse()]);

        Assert.Equal(ScoreboardOutcome.DifferentMatch, await PressAsync(match));
        Assert.True(match.Builds.IsEmpty);
    }
}
