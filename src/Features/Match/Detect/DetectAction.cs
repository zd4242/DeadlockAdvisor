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

    private readonly Subject<Unit> _artWanted = new();

    private readonly IDataService _data;
    private readonly ISettingsService _settings;
    private readonly IModalService _modals;
    private readonly IScreenCaptureService _capture;
    private readonly ILoggingService _log;

    public DetectAction(IDataService data, ISettingsService settings, IModalService modals, IScreenCaptureService capture, ILoggingService log)
    {
        _data = data;
        _settings = settings;
        _modals = modals;
        _capture = capture;
        _log = log;
    }

    public string TopbarDir => Path.Combine(_data.AssetsDir, "topbar");

    /// <summary>Asked for when there's no art to match against and the offer to download it is taken.</summary>
    public IObservable<Unit> ArtWanted => _artWanted;

    /// <summary>Run a detection into <paramref name="match"/>; <paramref name="applied"/> is called if the review is applied.</summary>
    public async Task RunAsync(MatchState match, Action applied)
    {
        // F9 while a review (or anything else) is up would stack a second detection behind it.
        if (_modals.IsModalOpen)
            return;

        var directory = TopbarDir;
        var bank = await Task.Run(() => TemplateBank.Load(directory));
        if (bank.IsEmpty)
        {
            _log.Warning($"Detect: no reference art in {directory}");
            _modals.Confirm($"There's no hero art to match against yet.\n\nDownload it from deadlock-api.com into {directory} now? "
                            + "It downloads in the background; press Detect again once it's done.",
                "Download", () => _artWanted.OnNext(Unit.Default), cancelText: "Not now");
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
            return;
        }

        var capturedAt = DateTimeOffset.UtcNow;
        var screenKey = $"{capture.ScreenWidth}x{capture.ScreenHeight}";
        var cached = _settings.Current.VisionGeometry.TryGetValue(screenKey, out var saved) ? Geometry.FromJson(saved) : null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var detection = await DetectWithProgressAsync(progress => Detect(capture, bank, cached, progress));
        var netWorth = detection is null
            ? NetWorthReading.Empty
            : await Task.Run(() => NetWorthReader.Read(capture.Band, detection.Geometry, NetWorthGlyphs.Bundled));
        var source = capture.FoundGame ? "Deadlock window" : "primary monitor (Deadlock's window not found)";
        _log.Information($"Detect: {capture.Band.Width}x{capture.Band.Height} band of a {screenKey} {source} at {capture.Origin.X},{capture.Origin.Y}, "
                         + $"{bank.Vectors.Count} reference image(s), {(cached is null ? "searched for the grid" : "cached grid")}: "
                         + (detection is null
                             ? "no strip found"
                             : $"{detection.ConfidentCount}/12 confident, you in slot {detection.SelfSlot?.ToString() ?? "unknown"}, "
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
                + "Detection reads the live scoreboard, so Deadlock needs to be in a match when you press Detect."
                + (capture.FoundGame ? "" : "\n\nDeadlock's window wasn't found, so the primary monitor was read."));
            return;
        }
        if (detection.ConfidentCount > 0)
            _settings.Update(s => s.VisionGeometry[screenKey] = detection.Geometry.ToJson());

        var heroes = _data.Store.Heroes.Values
            .Select(hero => new HeroChoice(hero.HeroId, hero.HeroName))
            .OrderBy(choice => choice.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();
        DetectReviewViewModel? review = null;
        review = new DetectReviewViewModel(detection, netWorth, heroes,
            async result =>
            {
                Close(review!);
                Apply(match, result, capturedAt, directory);
                applied();
                await KeepCaptureAsync(capture, capturedAt,
                    LabeledCapture.FromApplied(detection, result.SlotHeroes, result.SelfSlot, result.CorrectedSlots, reviewed: true,
                        capture.ScreenWidth, capture.ScreenHeight));
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
            netWorth: (result.SlotSouls, capturedAt));

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

    /// <summary>Keep an applied capture with the heroes it was applied as, the corpus detection is measured and tuned on.</summary>
    private async Task KeepCaptureAsync(ScreenCapture capture, DateTimeOffset capturedAt, LabeledCapture labels)
    {
        if (!_settings.Current.KeepDetectionCaptures)
            return;
        var note = labels.ToJson().ToJsonString(_captureJson);
        if (await Task.Run(() => CaptureArchive.Detections.Save(_data.DataRoot, capture.Band, capturedAt, note)) is { } path)
            _log.Information($"Detect: kept the applied capture in {path}");
    }

    private void Close(DetectReviewViewModel review)
    {
        _modals.CloseModal();
        review.Dispose();
    }
}
