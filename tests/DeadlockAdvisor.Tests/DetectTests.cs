using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match;
using DeadlockAdvisor.Features.Match.Detect;
using DeadlockAdvisor.Features.Match.Results;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Vision;
using static DeadlockAdvisor.Tests.Support.VisionData;

namespace DeadlockAdvisor.Tests;

/// <summary>Detect from screen end to end: a canned capture, the review, and what lands in the match.</summary>
public sealed class DetectTests : IDisposable
{
    // screen_2560x1440_band_2.json: you're Apollo in slot 0, laning with Ivy against Celeste and Lash.
    private static readonly string[] _band2Heroes =
        ["apollo", "ivy", "wraith", "bebop", "silver", "doorman", "celeste", "lash", "venator", "paige", "mirage", "mo_and_krill"];

    private readonly DataFixture _fixture = new();
    private readonly FakeScreenCapture _capture = new();
    private readonly List<ViewModelBase> _shown = [];
    private readonly IDisposable _watchModals;
    private readonly DetectAction _detect;
    private readonly MatchViewModel _page;

    public DetectTests()
    {
        _watchModals = _fixture.Modals.ShowModalObservable.Subscribe(_shown.Add);
        _detect = new DetectAction(_fixture.Data, _fixture.Settings, _fixture.Modals, _capture, new FakeLoggingService());
        _page = new MatchViewModel(_fixture.Data, _fixture.Settings, _detect);
    }

    public void Dispose()
    {
        _watchModals.Dispose();
        _page.Dispose();
        _fixture.Dispose();
    }

    private async Task<DetectReviewViewModel> DetectAsync()
    {
        CopyTopbarInto(_fixture.Data.AssetsDir);
        _capture.Next = Capture();
        await _page.DetectCommand.Execute();
        return Assert.IsType<DetectReviewViewModel>(_shown[^1]);
    }

    [AvaloniaFact]
    public async Task DetectingReadsTheScreenAndAppliesTheReviewedMatch()
    {
        var review = await DetectAsync();

        Assert.Equal(0, review.SelfSlot);
        Assert.Equal(_band2Heroes, review.Slots.Select(slot => slot.HeroId));
        Assert.Equal("YOU", review.Slots[0].RoleText);
        Assert.Equal([SlotRole.LaneAlly, SlotRole.LaneEnemy, SlotRole.LaneEnemy], new[] { 1, 6, 7 }.Select(i => review.Slots[i].Role));
        Assert.Equal(Enumerable.Range(0, 6), review.OwnRows.Select(slot => slot.Index));
        Assert.All(review.Slots, slot => Assert.NotNull(slot.Thumbnail));
        Assert.Contains("2560x1440", _fixture.Settings.Current.VisionGeometry.Keys);

        await review.ApplyCommand.Execute();

        Assert.False(_fixture.Modals.IsModalOpen);
        var match = _page.Match;
        Assert.Equal("apollo", match.SelfHero);
        Assert.Equal(_band2Heroes[1..6], match.Allies);
        Assert.Equal(_band2Heroes[6..], match.Enemies);
        Assert.Equal(["ivy", "celeste", "lash", "apollo"], match.LaneHeroes);
        // The match bar keeps the game's order, you in your own place included.
        Assert.Equal(_band2Heroes[..6], _page.Board.AllySlots.Select(slot => slot.HeroId));
        Assert.Equal(_band2Heroes[6..], _page.Board.EnemySlots.Select(slot => slot.HeroId));
        Assert.True(_page.Board.AllySlots[0].IsSelf);
        Assert.Equal("self", _fixture.Settings.Current.LastMatch!.Roles["apollo"]);
        Assert.False(_page.FullResults.IsEmpty);
    }

    [AvaloniaFact]
    public async Task NetWorthIsReadWithTheMatchAndShownOnTheBar()
    {
        int[] souls = [16_000, 13_000, 20_000, 12_000, 18_000, 12_000, 17_000, 13_000, 14_000, 15_000, 19_000, 15_000];
        var review = await DetectAsync();

        Assert.Equal(["16k", "13k", "20k", "12k", "18k", "12k", "17k", "13k", "14k", "15k", "19k", "15k"], review.Slots.Select(slot => slot.NetWorth));
        Assert.Contains("Net worth read for 12 of 12", review.Summary);
        await review.ApplyCommand.Execute();

        var match = _page.Match;
        Assert.Equal(souls, _band2Heroes.Select(hero => match.NetWorth.Latest(hero) ?? 0));
        Assert.Equal(Enumerable.Range(0, 12), _band2Heroes.Select(hero => match.Slots[hero]));
        var board = _page.Board;
        Assert.True(board.HasNetWorth);
        Assert.Equal(souls[..6], board.AllySlots.Select(slot => slot.NetWorth ?? 0));
        Assert.All(board.EnemySlots, slot => Assert.True(slot.ShowsNetWorth));
        Assert.Equal(("91k", "93k", "-2.2%", true), (board.AllyNetWorth, board.EnemyNetWorth, board.NetWorthLead, board.IsBehind));

        // Saved with the match, so reopening keeps it.
        Assert.Equal(souls, _band2Heroes.Select(hero => _fixture.Settings.Current.LastMatch!.NetWorth.Single().Souls[hero]));
    }

    [AvaloniaFact]
    public async Task ScoresLeanOnTheNetWorthReadUnlessToggledOff()
    {
        await (await DetectAsync()).ApplyCommand.Execute();
        _page.ResultsTab = 1;
        _page.FullResults.Select(_page.FullResults.Entries.OfType<ResultRowViewModel>().First());

        // 184k over twelve heroes averages 15.3k.
        var weighted = _page.Explain.Contributions.Where(card => card.NetWorthText is not null).ToList();
        Assert.NotEmpty(weighted);
        Assert.All(weighted, card => Assert.Matches(@"^×\d\.\d\d · \d+k vs 15k avg$", card.NetWorthText!));

        _page.ByNetWorth = false;

        Assert.All(_page.Explain.Contributions, card => Assert.Null(card.NetWorthText));
        Assert.False(_fixture.Settings.Current.ResultsByNetWorth);
    }

    [Fact]
    public void CapturesNetWorthWasNotReadOffAreKeptUpToALimit()
    {
        var start = new DateTimeOffset(2026, 9, 26, 20, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < NetWorthCaptures.Keep + 3; i++)
            Assert.NotNull(NetWorthCaptures.Save(_fixture.Data.DataRoot, new RgbImage(4, 4), start.AddSeconds(i), $"read {i}"));

        var folder = Path.Combine(_fixture.Data.DataRoot, NetWorthCaptures.FolderName);
        var kept = Directory.GetFiles(folder, "*.png").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(NetWorthCaptures.Keep, kept.Count);
        Assert.Equal("read 3", File.ReadAllText(Path.ChangeExtension(kept[0], ".txt")).Trim());
        Assert.Equal(NetWorthCaptures.Keep, Directory.GetFiles(folder, "*.txt").Length);
    }

    [AvaloniaFact]
    public async Task DetectingTheSameMatchAgainAddsToTheHistory()
    {
        await (await DetectAsync()).ApplyCommand.Execute();
        await (await DetectAsync()).ApplyCommand.Execute();

        Assert.Equal(2, _page.Match.NetWorth.Snapshots.Count);
        Assert.Equal(0, _page.Match.NetWorth.Change("apollo")?.Souls);

        _page.Board.ClearCommand.Execute().Subscribe();
        Assert.True(_page.Match.NetWorth.IsEmpty);
        Assert.False(_page.Board.HasNetWorth);
    }

    [AvaloniaFact]
    public async Task ASecondDetectionReusesTheCachedGrid()
    {
        var first = await DetectAsync();
        await first.CancelCommand.Execute();
        var cached = Geometry.FromJson(_fixture.Settings.Current.VisionGeometry["2560x1440"]);

        var second = await DetectAsync();

        Assert.NotNull(cached);
        Assert.Equal(_band2Heroes, second.Slots.Select(slot => slot.HeroId));
        Assert.Null(_page.Match.SelfHero);
    }

    [AvaloniaFact]
    public async Task CorrectionsAreKeptAsReferenceArt()
    {
        var review = await DetectAsync();
        review.Slots[3].SelectedHero = review.Slots[3].Choices.Single(choice => choice.HeroId == "haze");
        review.Slots[4].SelectedHero = HeroChoice.Unknown;

        await review.ApplyCommand.Execute();

        var saved = Path.Combine(_detect.TopbarDir, "haze", "variant_01.png");
        Assert.True(File.Exists(saved));
        Assert.False(Directory.Exists(Path.Combine(_detect.TopbarDir, "silver", "variant_01.png")));
        Assert.Equal("Saved as reference art", Assert.IsType<MessageModalViewModel>(_shown[^1]).Title);
        Assert.Equal(Role.Ally, _page.Match.RoleOf("haze"));
        Assert.Equal(Role.None, _page.Match.RoleOf("silver"));
    }

    [AvaloniaFact]
    public async Task CorrectionsAreNotKeptWhenAskedNotTo()
    {
        var review = await DetectAsync();
        review.Slots[3].SelectedHero = review.Slots[3].Choices.Single(choice => choice.HeroId == "haze");
        review.RememberCorrections = false;

        await review.ApplyCommand.Execute();

        Assert.False(Directory.Exists(Path.Combine(_detect.TopbarDir, "haze")));
        Assert.False(_fixture.Modals.IsModalOpen);
    }

    [AvaloniaFact]
    public async Task WithoutYouTheTeamsCantBeSplitUntilYouArePicked()
    {
        var detected = await DetectAsync();
        await detected.CancelCommand.Execute();
        var detection = DetectAction.Detect(Capture(), TemplateBank.Load(_detect.TopbarDir), null)! with { SelfSlot = null };
        using var review = new DetectReviewViewModel(detection, NetWorthReading.Empty, [], _ => { }, () => { });

        Assert.False(await review.ApplyCommand.CanExecute.FirstAsync());
        Assert.Equal("LEFT SIDE", review.OwnHeading);
        Assert.Contains("Press You on your own slot", review.Summary);
        Assert.All(review.Slots, slot => Assert.Equal(SlotRole.Unknown, slot.Role));

        review.Slots[8].SetSelfCommand.Execute().Subscribe();

        Assert.True(await review.ApplyCommand.CanExecute.FirstAsync());
        Assert.Equal(8, review.SelfSlot);
        Assert.Equal("YOUR TEAM", review.OwnHeading);
        Assert.Equal(Enumerable.Range(6, 6), review.OwnRows.Select(slot => slot.Index));
        Assert.Equal(SlotRole.LaneAlly, review.Slots[9].Role);
        Assert.Equal(SlotRole.Enemy, review.Slots[0].Role);
    }

    [AvaloniaFact]
    public async Task TheLaneAppliedIsTheLaneTheReviewShowed()
    {
        var detected = await DetectAsync();
        await detected.CancelCommand.Execute();
        // You in slot 0, with the game's marks on 3, 8 and 9 rather than the pairing's 1, 6 and 7.
        double[] marks = [9.0, 0.2, 0.3, 6.0, 0.2, 0.1, 0.3, 0.2, 5.0, 4.0, 0.2, 0.1];
        var detection = DetectAction.Detect(Capture(), TemplateBank.Load(_detect.TopbarDir), null)! with { SelfSlot = 0, SelfScores = marks };
        DetectReviewResult? applied = null;
        using var review = new DetectReviewViewModel(detection, NetWorthReading.Empty, [], result => applied = result, () => { });

        Assert.Equal([SlotRole.LaneAlly, SlotRole.LaneEnemy, SlotRole.LaneEnemy], new[] { 3, 8, 9 }.Select(i => review.Slots[i].Role));
        Assert.Equal(SlotRole.Ally, review.Slots[1].Role);
        await review.ApplyCommand.Execute();

        Assert.Equal([3, 8, 9], applied!.LaneSlots);
    }

    [AvaloniaFact]
    public async Task NoReferenceArtSaysWhereToGetIt()
    {
        _capture.Next = Capture();

        await _page.DetectCommand.Execute();

        Assert.Equal("No reference art", Assert.IsType<MessageModalViewModel>(_shown[^1]).Title);
        Assert.Equal(0, _capture.Captures);
    }

    [AvaloniaFact]
    public async Task AFailedCaptureSaysSo()
    {
        CopyTopbarInto(_fixture.Data.AssetsDir);

        await _page.DetectCommand.Execute();

        var message = Assert.IsType<MessageModalViewModel>(_shown[^1]);
        Assert.Equal("Could not capture the screen", message.Title);
        Assert.Equal("No screen in tests.", message.Body);
    }

    [AvaloniaFact]
    public async Task ABlankScreenReadsNoHeroesAndCachesNoGrid()
    {
        CopyTopbarInto(_fixture.Data.AssetsDir);
        _capture.Next = new(new RgbImage(2560, 316), 2560, 1440);

        await _page.DetectCommand.Execute();

        var review = Assert.IsType<DetectReviewViewModel>(_shown[^1]);
        Assert.All(review.Slots, slot => Assert.Null(slot.HeroId));
        Assert.Null(review.SelfSlot);
        Assert.Empty(_fixture.Settings.Current.VisionGeometry);
    }

    [AvaloniaFact]
    public async Task ACaptureTooSmallForTheStripFindsNothing()
    {
        CopyTopbarInto(_fixture.Data.AssetsDir);
        _capture.Next = new(new RgbImage(200, 64), 2560, 1440);

        await _page.DetectCommand.Execute();

        Assert.Equal("Nothing found", Assert.IsType<MessageModalViewModel>(_shown[^1]).Title);
    }
}
