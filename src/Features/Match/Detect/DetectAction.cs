using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using DeadlockAdvisor.Features.Shared.Modals.Confirmation;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Features.Shared.Modals.Progress;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Vision;
using ReactiveUI;

namespace DeadlockAdvisor.Features.Match.Detect;

/// <summary>
/// Detect from screen, start to finish: capture, detect (reusing the grid cached for this screen
/// size), review, apply, and keep any corrected crops as reference art.
/// </summary>
public class DetectAction
{
    /// <summary>
    /// A cached grid fitting worse than this (<see cref="Detection.Fit"/>) is searched for afresh and
    /// the better read kept: it has stopped fitting, say after a change of HUD scale. Counting
    /// confident slots instead paid for a five-second search whenever a few players were dead.
    /// </summary>
    public const double CacheTrustFit = 0.7;

    // A read with a cached grid is quick; only a search is worth putting a progress modal up for.
    private static readonly TimeSpan _progressDelay = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions _captureJson = new() { WriteIndented = true };

    private static readonly TimeSpan _appliedToast = TimeSpan.FromSeconds(6);

    private readonly Subject<Unit> _artWanted = new();
    private readonly BehaviorSubject<bool> _canReview = new(false);
    private readonly Subject<DetectOutcome> _finished = new();

    private readonly IDataService _data;
    private readonly ISettingsService _settings;
    private readonly IModalService _modals;
    private readonly INotificationService _notifications;
    private readonly IScreenCaptureService _capture;
    private readonly ILoggingService _log;
    private readonly IConnectivityService _connectivity;
    private Read? _lastApplied;
    private Unlabelled? _unlabelled;

    public DetectAction(IDataService data, ISettingsService settings, IModalService modals, INotificationService notifications,
        IScreenCaptureService capture, ILoggingService log, IConnectivityService connectivity)
    {
        _data = data;
        _settings = settings;
        _modals = modals;
        _notifications = notifications;
        _capture = capture;
        _log = log;
        _connectivity = connectivity;
    }

    public string TopbarDir => Path.Combine(_data.AssetsDir, "topbar");

    /// <summary>Asked for when there's no art to match against and the offer to download it is taken.</summary>
    public IObservable<Unit> ArtWanted => _artWanted;

    /// <summary>Whether there's a detection applied without review to look back at.</summary>
    public IObservable<bool> CanReview => _canReview;

    /// <summary>How each run ended, once whatever it needs the user for is up.</summary>
    public IObservable<DetectOutcome> Finished => _finished;

    /// <summary>
    /// Run a detection into <paramref name="match"/>: applied straight away when nothing in it is in
    /// doubt (and the setting allows), otherwise shown for review. <paramref name="applied"/> is called
    /// once it's applied.
    /// </summary>
    public async Task RunAsync(MatchState match, Action applied)
    {
        // F9 while a review (or anything else) is up would stack a second detection behind it.
        if (_modals.IsModalOpen)
            return;
        ForgetLast();

        var directory = TopbarDir;
        var bank = await Task.Run(() => TemplateBank.Load(directory));
        if (bank.IsEmpty)
        {
            _log.Warning($"Detect: no reference art in {directory}");
            if (_connectivity.IsOffline)
                _modals.ShowMessage("No hero art to match against",
                    "It downloads from deadlock-api.com, and you're offline. Connect to the internet, then press Detect again to download it.");
            else
                _modals.Confirm($"There's no hero art to match against yet.\n\nDownload it from deadlock-api.com into {directory} now? "
                                + "It downloads in the background; press Detect again once it's done.",
                    "Download", () => _artWanted.OnNext(Unit.Default), cancelText: "Not now");
            _finished.OnNext(DetectOutcome.NoArt);
            return;
        }

        ScreenCapture capture;
        try
        {
            capture = await _capture.CaptureTopBandAsync(_settings.Current.MinimizeToDetect);
        }
        catch (CaptureException ex)
        {
            _log.Warning($"Detect: capture failed: {ex.Message}");
            _modals.ShowMessage("Could not capture the screen", ex.Message);
            _finished.OnNext(DetectOutcome.CaptureFailed);
            return;
        }

        var capturedAt = DateTimeOffset.UtcNow;
        var screenKey = $"{capture.ScreenWidth}x{capture.ScreenHeight}";
        var cached = _settings.Current.VisionGeometry.TryGetValue(screenKey, out var saved) ? Geometry.FromJson(saved) : null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var detection = await DetectWithProgressAsync(progress => Detect(capture, bank, cached, progress));
        var rejectedFit = detection is { FoundStrip: false } ? detection.Fit : (double?)null;
        if (rejectedFit is not null)
            detection = null;
        if (detection is not null)
        {
            var roster = match.Slots.ToDictionary(pair => pair.Value, pair => pair.Key);
            int? rosterSelf = match.SelfHero is { } self && match.Slots.TryGetValue(self, out var selfSlot) ? selfSlot : null;
            detection = RosterContinuity.Apply(detection, roster, rosterSelf);
        }
        var netWorth = detection is null
            ? NetWorthReading.Empty
            : await Task.Run(() => NetWorthReader.Read(capture.Band, detection.Geometry, NetWorthGlyphs.Bundled));
        var source = capture.FoundGame ? "Deadlock window" : "primary monitor (Deadlock's window not found)";
        _log.Information($"Detect: {capture.Band.Width}x{capture.Band.Height} band of a {screenKey} {source} at {capture.Origin.X},{capture.Origin.Y}, "
                         + $"{bank.Vectors.Count} reference image(s), {(cached is null ? "searched for the grid" : "cached grid")}: "
                         + (detection is null
                             ? rejectedFit is { } fit ? $"no strip found (best fit {fit:0.00}, under {Detector.MinFit:0.00})" : "no strip found"
                             : (detection.BlankSlots.Count > 0 ? "Street Brawl, " : "")
                               + $"{detection.ConfidentCount}/12 confident, {detection.Slots.Count(slot => slot.Kept)} kept from the match, "
                               + $"you in slot {detection.SelfSlot?.ToString() ?? "unknown"}, fit {detection.Fit:0.00}, "
                               + NetWorthLog(netWorth))
                         + $", {clock.ElapsedMilliseconds} ms");
        if (_settings.Current.KeepUnreadCaptures && detection is not null && !(netWorth.Agrees(0) && netWorth.Agrees(1)))
        {
            var note = $"{NetWorthLog(netWorth)}\ngrid: {detection.Geometry.ToJson().ToJsonString()}";
            if (await Task.Run(() => CaptureArchive.NetWorth.Save(_data.DataRoot, capture.Band, capturedAt, note)) is { } path)
                _log.Information($"Detect: kept the capture net worth wasn't fully read off, in {path}");
        }
        if (detection is null)
        {
            _modals.ShowMessage("Nothing found",
                "Could not find the hero strip along the top of the screen.\n\n"
                + "Detection reads the live scoreboard, so Deadlock needs to be in a match, not a lobby or a loading screen, "
                + "with its top bar showing, when you press Detect.\n\n"
                + "A game that captures as a black screen can't be read either: try borderless windowed instead of exclusive fullscreen, "
                + "or turn HDR off."
                + (capture.FoundGame ? "" : "\n\nDeadlock's window wasn't found, so the primary monitor was read."));
            _finished.OnNext(DetectOutcome.NothingFound);
            return;
        }
        if (detection.ConfidentCount > 0)
            _settings.Update(s => s.VisionGeometry[screenKey] = detection.Geometry.ToJson());

        var read = new Read(detection, netWorth, capture, capturedAt, directory);
        if (_settings.Current.AutoApplyDetect && detection.HeroesSettled)
            await ApplyWithoutReviewAsync(match, applied, read);
        else
        {
            ShowReview(match, applied, read);
            _finished.OnNext(DetectOutcome.NeedsReview);
        }
    }

    /// <summary>Open the review of the detection last applied without one, to check or correct it.</summary>
    public void ReviewLast(MatchState match, Action applied)
    {
        if (!_modals.IsModalOpen && _lastApplied is { } read)
            ShowReview(match, applied, read);
    }

    /// <summary>The detection last applied without review is no longer the match's, so there's nothing to review.</summary>
    public void ForgetLast()
    {
        _lastApplied = null;
        _unlabelled = null;
        _canReview.OnNext(false);
    }

    /// <summary>
    /// Once you've been picked by hand on a match applied without knowing who you were, write that into the
    /// kept capture's label: a strip that hid you is exactly a capture to measure the next change on.
    /// </summary>
    public void LabelSelf(MatchState match)
    {
        if (_unlabelled is not { } pending || match.SelfHero is not { } self || !match.Slots.TryGetValue(self, out var slot))
            return;
        if (!match.Slots.All(entry => entry.Value < pending.Heroes.Count && pending.Heroes[entry.Value] == entry.Key))
            return;

        _unlabelled = null;
        var note = Path.ChangeExtension(pending.Image, CaptureArchive.Detections.NoteExtension);
        if (!File.Exists(note))
            return;
        pending.Labels.SelfSlot = slot;
        pending.Labels.Notes.Add("You were picked by hand on the match page: the strip's backplate didn't show which slot.");
        try
        {
            File.WriteAllText(note, pending.Labels.ToJson().ToJsonString(_captureJson) + Environment.NewLine);
            _log.Information($"Detect: labelled the kept capture with you in slot {slot}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Every slot read confidently or kept from the match, and you known: nothing for anyone to
    /// check, so it's applied, and the review stays a click away. Without you, the heroes are applied
    /// all the same and the match page asks for a click on yours, which is what splits the teams.
    /// </summary>
    private async Task ApplyWithoutReviewAsync(MatchState match, Action applied, Read read)
    {
        var heroes = read.Detection.Slots.Select(slot => slot.HeroId).ToList();
        var self = read.Detection.SelfSlot;
        var likely = self is null ? read.Detection.LikelyYou : null;
        Apply(match, new DetectReviewResult(heroes, self, [], read.NetWorth.Souls, [], LikelySelfSlot: likely), read.At, read.Directory);
        applied();
        _lastApplied = read;
        _canReview.OnNext(true);
        if (self is null)
        {
            _log.Information("Detect: applied the heroes without review, every slot being settled but you not found"
                             + (likely is { } slot ? $" (slot {slot} has a kill streak's backplate)" : ""));
            _notifications.ShowInformation("Read all twelve heroes, but not which one is you. Click your hero on the match bar.", _appliedToast);
            _finished.OnNext(DetectOutcome.NeedsYou);
        }
        else
        {
            _log.Information("Detect: applied without review, every slot being settled");
            _notifications.ShowSuccess("Read the match off the screen and applied it. Review beside Detect shows what was read.", _appliedToast);
            _finished.OnNext(DetectOutcome.Applied);
        }
        var labels = LabeledCapture.FromApplied(read.Detection, heroes, self, [], reviewed: false, read.Capture.ScreenWidth, read.Capture.ScreenHeight);
        var kept = await KeepCaptureAsync(read.Capture, read.At, labels);
        if (self is null && kept is not null)
            _unlabelled = new Unlabelled(kept, labels, heroes);
    }

    private void ShowReview(MatchState match, Action applied, Read read)
    {
        var heroes = _data.Store.Heroes.Values
            .Select(hero => new HeroChoice(hero.HeroId, hero.HeroName))
            .OrderBy(choice => choice.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();
        DetectReviewViewModel? review = null;
        review = new DetectReviewViewModel(read.Detection, read.NetWorth, heroes,
            async result =>
            {
                Close(review!);
                Apply(match, result, read.At, read.Directory);
                applied();
                await KeepCaptureAsync(read.Capture, read.At,
                    LabeledCapture.FromApplied(read.Detection, result.SlotHeroes, result.SelfSlot, result.CorrectedSlots, reviewed: true,
                        read.Capture.ScreenWidth, read.Capture.ScreenHeight));
            },
            () => Close(review!))
        {
            RememberCorrections = _settings.Current.RememberCorrections,
        };
        review.WhenAnyValue(vm => vm.RememberCorrections)
            .Skip(1)
            .Subscribe(remember => _settings.Update(s => s.RememberCorrections = remember));
        _modals.ShowModal(review);
    }

    /// <summary>A detection with everything needed to apply or review it: what was read, and off what.</summary>
    private sealed record Read(Detection Detection, NetWorthReading NetWorth, ScreenCapture Capture, DateTimeOffset At, string Directory);

    /// <summary>The kept capture of a match applied without knowing who you were, and the heroes it was applied as, by slot.</summary>
    private sealed record Unlabelled(string Image, LabeledCapture Labels, IReadOnlyList<string?> Heroes);

    /// <summary>What was read, for the log: each side's pills and total, and whether they added up.</summary>
    internal static string NetWorthLog(NetWorthReading reading)
    {
        string Side(int side)
        {
            var pills = reading.Pills.Skip(side * Layout.PerTeam).Take(Layout.PerTeam)
                .Select(souls => souls is { } value ? Format.Compact(value) : "?");
            var total = reading.Totals[side] is { } value ? Format.Compact(value) : "?";
            var verdict = reading.Agrees(side) ? "" : reading.Plausible(side) ? " (partly read, kept)" : " (doesn't add up, dropped)";
            return $"{string.Join(" ", pills)} = {total}{verdict}";
        }

        return $"net worth {Side(0)} | {Side(1)}";
    }

    internal static Detection? Detect(ScreenCapture capture, TemplateBank bank, Geometry? cached, IProgress<double>? progress = null)
    {
        // The band is only the top of the screen; the search sizes itself against the whole screen's height.
        var detection = Detector.Detect(capture.Band, bank, cached, screenHeight: capture.ScreenHeight, progress: progress);
        if (cached is not null && detection is not null && detection.Fit < CacheTrustFit)
        {
            // The HUD scale changed, or the cached grid was never as good as it looked: pay for a fresh
            // search and keep whichever fits better.
            var fresh = Detector.Detect(capture.Band, bank, screenHeight: capture.ScreenHeight, progress: progress);
            if (fresh is not null && fresh.Fit > detection.Fit)
                detection = fresh;
        }
        return detection;
    }

    private async Task<Detection?> DetectWithProgressAsync(Func<IProgress<double>, Detection?> detect)
    {
        using var progress = new ProgressModalViewModel("Detect from screen", "Reading the match off the top bar…", canCancel: false);
        var reporter = new Progress<double>(fraction =>
            progress.Report(new FetchProgress((int)Math.Round(fraction * 100), 100, "Searching for the hero strip")));
        var job = Task.Run(() => detect(reporter));
        var shown = await Task.WhenAny(job, Task.Delay(_progressDelay)) != job;
        if (shown)
            _modals.ShowModal(progress);
        try
        {
            return await job;
        }
        finally
        {
            if (shown)
                _modals.CloseModal();
        }
    }

    private void Apply(MatchState match, DetectReviewResult result, DateTimeOffset capturedAt, string directory)
    {
        VisionApply.ApplyToMatch(match, result.SlotHeroes, result.SelfSlot, _data.Store.Heroes.Keys,
            netWorth: (result.SlotSouls, capturedAt), likelySelfSlot: result.LikelySelfSlot);
        _unlabelled = null;

        var learned = 0;
        foreach (var (heroId, crop) in result.Corrections)
        {
            try
            {
                TemplateBank.SaveVariant(directory, heroId, crop);
                learned++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        if (learned > 0)
        {
            var faded = result.TooFaded > 0
                ? $"\n\n{result.TooFaded} other correction(s) weren't kept: the portrait was too faded (out of sight, or dead) to learn from."
                : "";
            _modals.ShowMessage("Saved as reference art",
                $"Kept {learned} corrected portrait(s) in {directory}.\nThose heroes should be recognised directly next time.{faded}");
        }
    }

    /// <summary>Keep an applied capture with the heroes it was applied as, the corpus detection is measured and tuned on; returns where, or null if it wasn't kept.</summary>
    private async Task<string?> KeepCaptureAsync(ScreenCapture capture, DateTimeOffset capturedAt, LabeledCapture labels)
    {
        if (!_settings.Current.KeepDetectionCaptures)
            return null;
        var note = labels.ToJson().ToJsonString(_captureJson);
        var path = await Task.Run(() => CaptureArchive.Detections.Save(_data.DataRoot, capture.Band, capturedAt, note));
        if (path is not null)
            _log.Information($"Detect: kept the applied capture in {path}");
        return path;
    }

    private void Close(DetectReviewViewModel review)
    {
        _modals.CloseModal();
        review.Dispose();
    }
}
