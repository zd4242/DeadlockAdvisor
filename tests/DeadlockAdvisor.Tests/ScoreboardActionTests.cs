using System.Reactive.Concurrency;
using DeadlockAdvisor.Features.Match.Scoreboard;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Vision;
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

    [Fact]
    public async Task WhatWasReadGoesToTheItemsHook()
    {
        WithArt();
        _capture.Next = Capture();
        ScoreboardRead? handed = null;
        _action.OnRead = (_, read) => handed = read;

        await PressAsync(Applied(_heroes));

        Assert.NotNull(handed?.Detection);
        Assert.Equal(_heroes, handed.Detection.Slots.Select(slot => slot.HeroId));
    }
}
