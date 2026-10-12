using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Vision;
using ReactiveUI;

namespace DeadlockAdvisor.Features.Match.Scoreboard;

/// <summary>How a press of the scoreboard key ended, for the log and for tests; none of them is ever shown to the player.</summary>
public enum ScoreboardOutcome
{
    /// <summary>The board was read and the match updated.</summary>
    Read,

    /// <summary>The setting is off.</summary>
    Off,

    /// <summary>An earlier press is still being read.</summary>
    Busy,

    /// <summary>A dialog is open, and anything done now would be seen.</summary>
    ModalOpen,

    /// <summary>No match is applied, so there is nothing to refresh.</summary>
    NoMatch,

    /// <summary>The key was let go before the board was up.</summary>
    Released,

    /// <summary>The heroes on the board aren't the match's.</summary>
    DifferentMatch,

    /// <summary>No hero strip was found, twice.</summary>
    NotFound,

    CaptureFailed,

    /// <summary>There is no hero art to read the board with.</summary>
    NoArt,
}

/// <summary>
/// The player holds the game's scoreboard key and the advisor quietly reads the board it shows: net worth now, and
/// each hero's items once the item reader exists. Silent by design (no window, focus, toast or sound, and a read of
/// anything but the match already applied is dropped), since it runs in the middle of a match.
/// </summary>
public sealed class ScoreboardAction
{
    /// <summary>The top of the screen the items on the board reach (WP24: 6.1 pitches, 50% of the height), with a little over.</summary>
    public const double CaptureFraction = 0.52;

    /// <summary>The board takes a moment to fade in after the key goes down.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>A second look, if the first found no board, about 600 ms after the key went down.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(350);

    private readonly IScoreboardKeyService _key;
    private readonly IScreenCaptureService _capture;
    private readonly ISettingsService _settings;
    private readonly IDataService _data;
    private readonly IModalService _modals;
    private readonly ILoggingService _log;
    private readonly IScheduler _scheduler;
    private (string Folder, DateTime Stamp, TemplateBank Bank)? _bank;
    private bool _running;

    public ScoreboardAction(IScoreboardKeyService key, IScreenCaptureService capture, ISettingsService settings, IDataService data,
        IModalService modals, ILoggingService log)
        : this(key, capture, settings, data, modals, log, RxApp.MainThreadScheduler)
    {
    }

    internal ScoreboardAction(IScoreboardKeyService key, IScreenCaptureService capture, ISettingsService settings, IDataService data,
        IModalService modals, ILoggingService log, IScheduler scheduler)
    {
        _key = key;
        _capture = capture;
        _settings = settings;
        _data = data;
        _modals = modals;
        _log = log;
        _scheduler = scheduler;
    }

    /// <summary>
    /// Called with what a successful read found, on the UI thread, after the match's net worth was updated. The item reader
    /// plugs in here.
    /// </summary>
    public Action<MatchState, ScoreboardRead>? OnRead { get; set; }

    private string TopbarDir => Path.Combine(_data.AssetsDir, "topbar");

    /// <param name="match">The match applied so far, which a read of the same match updates.</param>
    /// <param name="changed">Called after the match changed, so the page can show it.</param>
    public async Task<ScoreboardOutcome> OnPressedAsync(MatchState match, Action changed)
    {
        if (_running)
            return Ignored(ScoreboardOutcome.Busy, "the last press is still being read");
        if (!_settings.Current.ReadBuildsOnTab)
            return Ignored(ScoreboardOutcome.Off, "reading the board is off");
        if (_modals.IsModalOpen)
            return Ignored(ScoreboardOutcome.ModalOpen, "a window is open");
        if (match.Slots.Count == 0)
            return Ignored(ScoreboardOutcome.NoMatch, "no match is applied");

        _running = true;
        try
        {
            await Wait(SettleDelay);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!_key.IsHeld)
                    return Ignored(ScoreboardOutcome.Released, "the key was let go before the board was read");
                var outcome = await TryReadAsync(match, changed);
                if (outcome != ScoreboardOutcome.NotFound)
                    return outcome;
                if (attempt == 0)
                    await Wait(RetryDelay);
            }
            return Ignored(ScoreboardOutcome.NotFound, "no hero strip was found on the board");
        }
        finally
        {
            _running = false;
        }
    }

    private async Task<ScoreboardOutcome> TryReadAsync(MatchState match, Action changed)
    {
        ScreenCapture capture;
        try
        {
            capture = await _capture.CaptureTopAsync(CaptureFraction);
        }
        catch (CaptureException ex)
        {
            return Ignored(ScoreboardOutcome.CaptureFailed, $"the screen couldn't be captured: {ex.Message}");
        }

        var directory = TopbarDir;
        var bank = await Task.Run(() => BankFor(directory));
        if (bank.IsEmpty)
            return Ignored(ScoreboardOutcome.NoArt, "there is no hero art to read with");

        var screenKey = $"{capture.ScreenWidth}x{capture.ScreenHeight}";
        var cached = _settings.Current.VisionGeometry.TryGetValue(screenKey, out var saved) ? Geometry.FromJson(saved) : null;
        var roster = match.Slots.ToDictionary(pair => pair.Value, pair => pair.Key);
        int? rosterSelf = match.SelfHero is { } self && match.Slots.TryGetValue(self, out var selfSlot) ? selfSlot : null;
        var read = await Task.Run(() => ScoreboardReader.Read(capture, bank, cached, roster, rosterSelf));
        if (read.Detection is not { } detection)
            return ScoreboardOutcome.NotFound;
        if (!RosterContinuity.IsSameMatch(detection, roster))
            return Ignored(ScoreboardOutcome.DifferentMatch, "the heroes on the board aren't the match's");

        if (detection.ConfidentCount > 0)
            _settings.Update(s => s.VisionGeometry[screenKey] = detection.Geometry.ToJson());
        var snapshot = VisionApply.SnapshotFor(match, read.NetWorth.Souls, DateTimeOffset.UtcNow);
        match.NetWorth.Add(snapshot);
        changed();
        OnRead?.Invoke(match, read);
        _log.Information($"Scoreboard: read the board, net worth for {snapshot.Souls.Count} of 12 heroes");
        return ScoreboardOutcome.Read;
    }

    /// <summary>The reference art, kept from one press to the next until something under the folder changes (a download, a learned portrait).</summary>
    private TemplateBank BankFor(string directory)
    {
        var stamp = StampOf(directory);
        if (_bank is { } kept && kept.Folder == directory && kept.Stamp == stamp)
            return kept.Bank;
        var bank = TemplateBank.Load(directory);
        _bank = (directory, stamp, bank);
        return bank;
    }

    private static DateTime StampOf(string directory)
    {
        if (!Directory.Exists(directory))
            return DateTime.MinValue;
        var latest = Directory.GetLastWriteTimeUtc(directory);
        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            var written = Directory.GetLastWriteTimeUtc(folder);
            if (written > latest)
                latest = written;
        }
        var derived = Path.Combine(directory, "_derived.json");
        var derivedAt = File.Exists(derived) ? File.GetLastWriteTimeUtc(derived) : DateTime.MinValue;
        return derivedAt > latest ? derivedAt : latest;
    }

    private Task Wait(TimeSpan delay) => Observable.Timer(delay, _scheduler).ToTask();

    private ScoreboardOutcome Ignored(ScoreboardOutcome outcome, string why)
    {
        _log.Information($"Scoreboard: {why}, so nothing was read");
        return outcome;
    }
}
