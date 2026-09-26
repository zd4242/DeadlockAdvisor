using System.IO;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Features.Shared.Modals.Progress;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Features.Match.Detect;

/// <summary>
/// Detect from screen, start to finish: capture, detect (reusing the grid cached for this screen
/// size), review, apply, and keep any corrected crops as reference art.
/// </summary>
public class DetectAction
{
    /// <summary>
    /// A cached grid reading fewer slots than this confidently is searched for afresh and the better
    /// read kept: a correct grid reads all twelve on a live match, so much less means a roster of dead
    /// players or a grid that has quietly stopped fitting.
    /// </summary>
    public const int CacheTrustFloor = 10;

    // A read with a cached grid is quick; only a search is worth putting a progress modal up for.
    private static readonly TimeSpan _progressDelay = TimeSpan.FromMilliseconds(250);

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
            _modals.ShowMessage("No reference art",
                $"There's no hero art to match against yet.\n\nData → Download Art… fetches it into {directory}.");
            return;
        }

        ScreenCapture capture;
        try
        {
            capture = await _capture.CaptureTopBandAsync();
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
        _log.Information($"Detect: {capture.Band.Width}x{capture.Band.Height} band of a {screenKey} screen, "
                         + $"{bank.Vectors.Count} reference image(s), {(cached is null ? "searched for the grid" : "cached grid")}: "
                         + (detection is null
                             ? "no strip found"
                             : $"{detection.ConfidentCount}/12 confident, you in slot {detection.SelfSlot?.ToString() ?? "unknown"}, "
                               + NetWorthLog(netWorth))
                         + $", {clock.ElapsedMilliseconds} ms");
        if (detection is null)
        {
            _modals.ShowMessage("Nothing found",
                "Could not find the hero strip along the top of the screen.\n\n"
                + "Detection reads the live scoreboard, so Deadlock needs to be in a match and on the primary monitor when you press Detect.");
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
            result =>
            {
                Close(review!);
                Apply(match, result, capturedAt, directory);
                applied();
            },
            () => Close(review!));
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
            return $"{string.Join(" ", pills)} = {total}{(reading.Agrees(side) ? "" : " (doesn't add up)")}";
        }

        return $"net worth {Side(0)} | {Side(1)}";
    }

    internal static Detection? Detect(ScreenCapture capture, TemplateBank bank, Geometry? cached, IProgress<double>? progress = null)
    {
        // The band is only the top of the screen; the search sizes itself against the whole screen's height.
        var detection = Detector.Detect(capture.Band, bank, cached, screenHeight: capture.ScreenHeight, progress: progress);
        if (cached is not null && detection is not null && detection.ConfidentCount < CacheTrustFloor)
        {
            // The HUD scale changed, or the cached grid was never as good as it looked: pay for a fresh
            // search and keep whichever reads better.
            var fresh = Detector.Detect(capture.Band, bank, screenHeight: capture.ScreenHeight, progress: progress);
            if (fresh is not null && fresh.ConfidentCount > detection.ConfidentCount)
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
        VisionApply.ApplyToMatch(match, result.SlotHeroes, result.SelfSlot, _data.Store.Heroes.Keys, laneSlots: result.LaneSlots,
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
            _modals.ShowMessage("Saved as reference art",
                $"Kept {learned} corrected portrait(s) in {directory}.\nThose heroes should be recognised directly next time.");
        }
    }

    private void Close(DetectReviewViewModel review)
    {
        _modals.CloseModal();
        review.Dispose();
    }
}
