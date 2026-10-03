using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match;
using DeadlockAdvisor.Features.Match.Detect;
using DeadlockAdvisor.Features.Match.Explain;
using DeadlockAdvisor.Features.Match.Import;
using DeadlockAdvisor.Features.Match.Results;
using DeadlockAdvisor.Features.Shared.Modals.Confirmation;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Services;
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

    /// <summary>A capture every hero of which reads confidently, you found too.</summary>
    private const string Certain = "screen_2560x1440_band";

    private readonly DataFixture _fixture = new();
    private readonly FakeScreenCapture _capture = new();
    private readonly List<ViewModelBase> _shown = [];
    private readonly IDisposable _watchModals;
    private readonly DetectAction _detect;
    private readonly MatchViewModel _page;

    public DetectTests()
    {
        _watchModals = _fixture.Modals.ShowModalObservable.Subscribe(_shown.Add);
        // Most of these are about the review, which a certain read would otherwise skip.
        _fixture.Settings.Current.AutoApplyDetect = false;
        _detect = new DetectAction(_fixture.Data, _fixture.Settings, _fixture.Modals, new NotificationService(new FakeLoggingService()), _capture, new FakeLoggingService());
        var dataRanks = new DataRanksViewModel(_fixture.Data, new MatchStatsService(new FakeDeadlockApi()), new NotificationService(new FakeLoggingService()));
        var import = new ImportMatchAction(_fixture.Data, _fixture.Settings, _fixture.Modals, new MatchLookupService(new FakeDeadlockApi()),
            new FakeLoggingService());
        _page = new MatchViewModel(_fixture.Data, _fixture.Settings, _detect, import, dataRanks);
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
        Assert.Equal([SlotRole.Ally, SlotRole.Enemy], new[] { 1, 6 }.Select(i => review.Slots[i].Role));
        Assert.Equal(Enumerable.Range(0, 6), review.OwnRows.Select(slot => slot.Index));
        Assert.All(review.Slots, slot => Assert.NotNull(slot.Thumbnail));
        Assert.Contains("2560x1440", _fixture.Settings.Current.VisionGeometry.Keys);

        await review.ApplyCommand.Execute();

        Assert.False(_fixture.Modals.IsModalOpen);
        var match = _page.Match;
        Assert.Equal("apollo", match.SelfHero);
        Assert.Equal(_band2Heroes[1..6], match.Allies);
        Assert.Equal(_band2Heroes[6..], match.Enemies);
        // The match bar keeps the game's order, you in your own place included.
        Assert.Equal(_band2Heroes[..6], _page.Board.AllySlots.Select(slot => slot.HeroId));
        Assert.Equal(_band2Heroes[6..], _page.Board.EnemySlots.Select(slot => slot.HeroId));
        Assert.True(_page.Board.AllySlots[0].IsSelf);
        Assert.Equal("self", _fixture.Settings.Current.LastMatch!.Roles["apollo"]);
        Assert.False(_page.Results.IsEmpty);

        // A new line-up opens on its best item, explained.
        var top = _page.Results.Entries.OfType<ResultRowViewModel>().First();
        Assert.True(top.IsSelected);
        Assert.Equal(top.ItemId, _page.Results.SelectedItemId);
        Assert.True(_page.Explain.HasItem);
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

        // 184k over twelve heroes averages 15.3k. A single-target item's rank comes first.
        static bool NetWorthNote(ContributionCard card) => card.Note?.EndsWith(" avg", StringComparison.Ordinal) == true;
        var weighted = _page.Explain.Contributions.Where(NetWorthNote).ToList();
        Assert.NotEmpty(weighted);
        Assert.All(weighted, card => Assert.Matches(@"(^|target ×[\d.]+ · )×\d\.\d\d · \d+k vs 15k avg$", card.Note!));

        _page.ByNetWorth = false;

        Assert.DoesNotContain(_page.Explain.Contributions, NetWorthNote);
        Assert.False(_fixture.Settings.Current.ResultsByNetWorth);
    }

    [Fact]
    public void CapturesAreKeptUpToALimitForEachKind()
    {
        var start = new DateTimeOffset(2026, 9, 26, 20, 0, 0, TimeSpan.Zero);
        var archive = CaptureArchive.NetWorth;
        Assert.NotNull(CaptureArchive.Detections.Save(_fixture.Data.DataRoot, new RgbImage(4, 4), start, "{}"));
        for (var i = 0; i < archive.Keep + 3; i++)
            Assert.NotNull(archive.Save(_fixture.Data.DataRoot, new RgbImage(4, 4), start.AddSeconds(i), $"read {i}"));

        var folder = Path.Combine(_fixture.Data.DataRoot, CaptureArchive.FolderName);
        var kept = Directory.GetFiles(folder, "networth_*.png").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(archive.Keep, kept.Count);
        Assert.Equal("read 3", File.ReadAllText(Path.ChangeExtension(kept[0], ".txt")).Trim());
        Assert.Equal(archive.Keep, Directory.GetFiles(folder, "*.txt").Length);
        // Pruning one kind leaves the others alone.
        Assert.Single(Directory.GetFiles(folder, "detect_*.png"));
        Assert.Single(Directory.GetFiles(folder, "detect_*.json"));
    }

    [AvaloniaFact]
    public async Task AnAppliedDetectionIsKeptWithTheHeroesItWasAppliedAs()
    {
        var review = await DetectAsync();
        review.Slots[3].SelectedHero = review.Slots[3].Choices.Single(choice => choice.HeroId == "haze");
        review.Slots[4].SelectedHero = HeroChoice.Unknown;
        review.RememberCorrections = false;

        await review.ApplyCommand.Execute();

        var folder = Path.Combine(_fixture.Data.DataRoot, CaptureArchive.FolderName);
        var note = Assert.Single(Directory.GetFiles(folder, "detect_*.json"));
        Assert.True(File.Exists(Path.ChangeExtension(note, ".png")));
        var kept = LabeledCapture.FromJson(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(note))!);
        Assert.Equal((2560, 1440, 0), (kept.ScreenWidth!.Value, kept.ScreenHeight!.Value, kept.SelfSlot!.Value));
        string?[] applied = [.. _band2Heroes];
        (applied[3], applied[4]) = ("haze", null);
        Assert.Equal(applied, Enumerable.Range(0, 12).Select(slot => kept.Heroes.GetValueOrDefault(slot)));
        Assert.Equal(LabelSource.Corrected, kept.SourceOf(3));
        Assert.True(kept.Reviewed);
        // What the detector said before the review changed it.
        Assert.Equal(_band2Heroes, kept.Read!.Select(slot => slot.Hero));
        Assert.Equal(0, kept.ReadSelfSlot);
        Assert.NotNull(kept.Grid);
    }

    [AvaloniaFact]
    public async Task AppliedDetectionsAreNotKeptWhenTurnedOff()
    {
        _fixture.Settings.Update(s => s.KeepDetectionCaptures = false);

        await (await DetectAsync()).ApplyCommand.Execute();

        var folder = Path.Combine(_fixture.Data.DataRoot, CaptureArchive.FolderName);
        Assert.Empty(Directory.Exists(folder) ? Directory.GetFiles(folder, "detect_*") : []);
    }

    [AvaloniaFact]
    public async Task DetectingTheSameMatchAgainAddsToTheHistory()
    {
        await (await DetectAsync()).ApplyCommand.Execute();
        var picked = _page.Results.Entries.OfType<ResultRowViewModel>().Skip(1).First();
        _page.Results.Select(picked);
        await (await DetectAsync()).ApplyCommand.Execute();

        // The same heroes again, say to update net worth, keep what you were reading about.
        Assert.Equal(picked.ItemId, _page.Results.SelectedItemId);
        Assert.Equal(2, _page.Match.NetWorth.Snapshots.Count);
        Assert.Equal(0, _page.Match.NetWorth.Change("apollo")?.Souls);

        _page.Board.ClearCommand.Execute().Subscribe();
        Assert.True(_page.Match.NetWorth.IsEmpty);
        Assert.False(_page.Board.HasNetWorth);
    }

    [AvaloniaFact]
    public async Task ACertainDetectionIsAppliedWithoutReviewAndCanStillBeReviewed()
    {
        _fixture.Settings.Current.AutoApplyDetect = true;
        CopyTopbarInto(_fixture.Data.AssetsDir);
        // Every hero in this one reads confidently, and you're found.
        _capture.Next = Capture(Certain);
        var spec = FixtureSpec(Certain);
        var heroes = Enumerable.Range(0, 12).Select(slot => (string)spec["heroes"]![slot.ToString()]!).ToList();

        await _page.DetectCommand.Execute();

        Assert.DoesNotContain(_shown, modal => modal is DetectReviewViewModel);
        Assert.False(_fixture.Modals.IsModalOpen);
        Assert.Equal(heroes[1], _page.Match.SelfHero);
        Assert.Equal(heroes[6..], _page.Match.Enemies);
        Assert.True(_page.Board.CanReviewDetection);
        var kept = Directory.GetFiles(Path.Combine(_fixture.Data.DataRoot, CaptureArchive.FolderName), "detect_*.json").Single();
        Assert.False(LabeledCapture.FromJson(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(kept))!).Reviewed);

        // Review shows what was read, and applying it again (corrected) works as ever.
        await _page.ReviewDetectionCommand.Execute();
        var review = Assert.IsType<DetectReviewViewModel>(_shown[^1]);
        Assert.Equal(heroes, review.Slots.Select(slot => slot.HeroId));
        review.Slots[3].SelectedHero = review.Slots[3].Choices.Single(choice => choice.HeroId == "haze");
        await review.ApplyCommand.Execute();
        Assert.Equal(Role.Ally, _page.Match.RoleOf("haze"));
    }

    [AvaloniaFact]
    public async Task AnUncertainDetectionIsReviewedEvenWhenApplyingWithoutAsking()
    {
        _fixture.Settings.Current.AutoApplyDetect = true;
        CopyTopbarInto(_fixture.Data.AssetsDir);
        var band = Capture().Band;
        var blacked = new RgbImage(band.Width, band.Height, (byte[])band.Pixels.Clone());
        // A dead player in slot 3, and no match applied yet to keep them from.
        var grid = DetectAction.Detect(Capture(), TemplateBank.Load(_detect.TopbarDir), null)!.Geometry;
        var dead = grid.Boxes()[3];
        for (var y = (int)dead.Y; y < (int)(dead.Y + dead.H); y++)
            Array.Clear(blacked.Pixels, (y * band.Width + (int)dead.X) * 3, (int)dead.W * 3);
        _capture.Next = new(blacked, 2560, 1440);

        await _page.DetectCommand.Execute();

        var review = Assert.IsType<DetectReviewViewModel>(_shown[^1]);
        Assert.True(review.Slots[3].IsUncertain);
        Assert.False(_page.Board.CanReviewDetection);
    }

    [AvaloniaFact]
    public async Task ClearingTheMatchLeavesNothingToReview()
    {
        _fixture.Settings.Current.AutoApplyDetect = true;
        CopyTopbarInto(_fixture.Data.AssetsDir);
        _capture.Next = Capture(Certain);
        await _page.DetectCommand.Execute();
        Assert.True(_page.Board.CanReviewDetection);

        _page.Board.ClearCommand.Execute().Subscribe();

        Assert.False(_page.Board.CanReviewDetection);
        Assert.False(await _page.ReviewDetectionCommand.CanExecute.FirstAsync());
    }

    [AvaloniaFact]
    public async Task DetectingAgainKeepsTheHeroAPortraitNoLongerShows()
    {
        await (await DetectAsync()).ApplyCommand.Execute();
        var grid = Geometry.FromJson(_fixture.Settings.Current.VisionGeometry["2560x1440"])!;
        var band = Capture().Band;
        var dead = grid.Boxes()[3];
        var blacked = new RgbImage(band.Width, band.Height, (byte[])band.Pixels.Clone());
        for (var y = (int)dead.Y; y < (int)(dead.Y + dead.H); y++)
            Array.Clear(blacked.Pixels, (y * band.Width + (int)dead.X) * 3, (int)dead.W * 3);

        _capture.Next = new(blacked, 2560, 1440);
        await _page.DetectCommand.Execute();
        var review = Assert.IsType<DetectReviewViewModel>(_shown[^1]);

        Assert.Equal(_band2Heroes, review.Slots.Select(slot => slot.HeroId));
        Assert.True(review.Slots[3].Reading.Kept);
        Assert.Equal("kept from your current match", review.Slots[3].Detail);
        Assert.False(review.Slots[3].IsUncertain);
        Assert.Matches(@"\(and kept \d+ from your current match\)", review.Summary);
        Assert.DoesNotContain(review.Slots, slot => slot.IsUncertain);
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
        // Framed by the grid, not by wherever the misread hero happened to score best.
        var grid = Geometry.FromJson(_fixture.Settings.Current.VisionGeometry["2560x1440"])!;
        var framed = Layout.Crop(Capture().Band, grid.Boxes()[3])!;
        Assert.Equal(framed.Pixels, ImageFile.Load(saved).Pixels);
        Assert.False(Directory.Exists(Path.Combine(_detect.TopbarDir, "silver", "variant_01.png")));
        Assert.Equal("Saved as reference art", Assert.IsType<MessageModalViewModel>(_shown[^1]).Title);
        Assert.Equal(Role.Ally, _page.Match.RoleOf("haze"));
        Assert.Equal(Role.None, _page.Match.RoleOf("silver"));
    }

    /// <summary>The choice only comes up once there's a correction, and it sticks, as the setting it's shared with.</summary>
    [AvaloniaFact]
    public async Task CorrectionsAreNotKeptWhenAskedNotTo()
    {
        var review = await DetectAsync();
        Assert.False(review.HasCorrections);
        review.Slots[3].SelectedHero = review.Slots[3].Choices.Single(choice => choice.HeroId == "haze");
        Assert.True(review.HasCorrections);
        review.RememberCorrections = false;

        await review.ApplyCommand.Execute();

        Assert.Empty(Directory.GetFiles(Path.Combine(_detect.TopbarDir, "haze"), "variant_*"));
        Assert.False(_fixture.Modals.IsModalOpen);
        Assert.False(_fixture.Settings.Current.RememberCorrections);
        Assert.False((await DetectAsync()).RememberCorrections);
    }

    [AvaloniaFact]
    public async Task WithoutYouTheTeamsCantBeSplitUntilYouArePicked()
    {
        var detected = await DetectAsync();
        await detected.CancelCommand.Execute();
        var detection = DetectAction.Detect(Capture(), TemplateBank.Load(_detect.TopbarDir), null)! with { SelfSlot = null };
        using var review = new DetectReviewViewModel(detection, NetWorthReading.Empty, [], _ => Task.CompletedTask, () => { });

        Assert.False(await review.ApplyCommand.CanExecute.FirstAsync());
        Assert.Equal("LEFT SIDE", review.OwnHeading);
        Assert.False(review.HasSelf);
        Assert.Contains("couldn't tell which one is you", review.Summary);
        Assert.All(review.Slots, slot => Assert.Equal(SlotRole.Unknown, slot.Role));

        review.Slots[8].SetSelfCommand.Execute().Subscribe();

        Assert.True(await review.ApplyCommand.CanExecute.FirstAsync());
        Assert.Equal(8, review.SelfSlot);
        Assert.Equal("YOUR TEAM", review.OwnHeading);
        Assert.Equal(Enumerable.Range(6, 6), review.OwnRows.Select(slot => slot.Index));
        Assert.Equal(SlotRole.Ally, review.Slots[9].Role);
        Assert.Equal(SlotRole.Enemy, review.Slots[0].Role);
    }

    [AvaloniaFact]
    public async Task NoReferenceArtOffersToDownloadIt()
    {
        _capture.Next = Capture();
        var wanted = 0;
        using var watch = _page.ArtWanted.Subscribe(_ => wanted++);

        await _page.DetectCommand.Execute();

        var offer = Assert.IsType<ConfirmationModalViewModel>(_shown[^1]);
        Assert.Contains("no hero art to match against", offer.Prompt);
        Assert.Equal(0, _capture.Captures);
        offer.ConfirmCommand!.Execute(null);
        Assert.Equal(1, wanted);
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

        var message = Assert.IsType<MessageModalViewModel>(_shown[^1]);
        Assert.Equal("Nothing found", message.Title);
        Assert.DoesNotContain("wasn't found", message.Body);
    }

    [AvaloniaFact]
    public void AReadSaysSureOrUnsureWithItsNumbersInTheTip()
    {
        HeroChoice[] choices = [HeroChoice.Unknown, new("haze", "Haze"), new("lash", "Lash")];
        SlotReviewViewModel Slot(double score, double margin) =>
            new(new SlotReading(0, "haze", score, margin, "lash", default, []), null, choices, _ => { });

        var sure = Slot(0.9, 0.5);
        Assert.Equal("Sure", sure.Detail);
        Assert.Equal("Matches the portrait 0.90 (1 is a perfect match), 0.50 ahead of the next best hero.", sure.DetailTip);

        var unsure = Slot(0.5, 0.01);
        Assert.Equal("Unsure: could be Lash", unsure.Detail);
        Assert.True(unsure.IsUncertain);

        unsure.SelectedHero = choices[2];
        Assert.Equal("corrected from Haze", unsure.Detail);
        Assert.Null(unsure.DetailTip);
    }

    [AvaloniaFact]
    public async Task FindingNothingWithoutTheGameWindowSaysThePrimaryMonitorWasRead()
    {
        CopyTopbarInto(_fixture.Data.AssetsDir);
        _capture.Next = new(new RgbImage(200, 64), 2560, 1440, FoundGame: false);

        await _page.DetectCommand.Execute();

        Assert.Contains("primary monitor was read", Assert.IsType<MessageModalViewModel>(_shown[^1]).Body);
    }
}
