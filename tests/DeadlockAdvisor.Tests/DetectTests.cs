using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match;
using DeadlockAdvisor.Features.Match.Detect;
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
        Assert.Equal(6, _page.Board.AllySlots.Count);
        Assert.Equal("self", _fixture.Settings.Current.LastMatch!.Roles["apollo"]);
        Assert.False(_page.FullResults.IsEmpty);
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
        using var review = new DetectReviewViewModel(detection, [], _ => { }, () => { });

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
