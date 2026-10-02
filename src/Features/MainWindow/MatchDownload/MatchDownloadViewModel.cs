using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow.MatchDownload;

/// <summary>What the download dialog was left at when Download was pressed.</summary>
/// <param name="Ranks">The rank groups choice, for downloads from deadlock-api.com; the shared download always has them.</param>
public sealed record MatchDownloadChoice(MatchDownloadPlan Plan, bool Ranks, bool KeepUpToDate);

/// <summary>
/// Before a match data download: what's stored, what this one fetches and keeps, and about how long it
/// takes and how much it brings. The shared download comes ready-made with the rank groups, in seconds;
/// when it isn't available, deadlock-api.com is asked directly, with or without the rank groups.
/// </summary>
public sealed class MatchDownloadViewModel : ViewModelBase
{
    public const string RanksInfo =
        "Rank groups split the matches by rank, so the Match tab's filters can lean the numbers toward the ranks you play.\n"
        + "They take about five times as long to download.";

    /// <summary>What "keep it up to date" does, for every place that offers it.</summary>
    public const string AutoUpdateInfo =
        "When the app starts, it checks for newer match data: one quick request.\n"
        + "If a new patch is out, a patch has ended since your last download, or the current patch's data is a day and a half old\n"
        + "(three days when the shared download isn't available and it has to ask deadlock-api.com itself),\n"
        + "it downloads just what changed, in the background. Patches you already have in full are never downloaded again.\n"
        + "It never starts a first download, and if an update fails it quietly tries again next time.\n"
        + "You can turn it off any time in Settings → Data.";

    /// <summary>The shared download takes this long at most, for the wording: a handful of small files.</summary>
    public const string SharedTime = "a few seconds";

    private readonly MatchDownloadPlan _everyMatch;
    private readonly MatchDownloadPlan _withRanks;

    /// <summary>The dialog for a download from deadlock-api.com, with or without the rank groups.</summary>
    /// <param name="download">Starts the chosen plan, with the choices made.</param>
    public MatchDownloadViewModel(IModalService modals, IReadOnlyList<MatchSegment> stored, MatchFetchPlan everyMatch, MatchFetchPlan withRanks,
        MatchFetchEstimate estimate, bool includeRanks, bool keepUpToDate, Action<MatchDownloadChoice> download)
        : this(modals, stored, everyMatch, withRanks, includeRanks, keepUpToDate, download)
    {
        Intro = "Item win rates from real matches on deadlock-api.com, shown beside each recommendation as a second opinion. "
                + "The shared download isn't available right now, so this asks deadlock-api.com directly. "
                + "Patches you already have in full are skipped.";
        EveryMatchDetail = Cost(everyMatch, estimate);
        WithRanksDetail = Cost(withRanks, estimate);
        Describe(plan => $"takes {MatchFetchEstimate.DescribeTime(estimate.Time((MatchFetchPlan)plan))}. "
                         + "It runs in the background, so you can keep using the app, and uses");
    }

    /// <summary>The dialog for the shared download: one choice, the rank groups included.</summary>
    /// <param name="includeRanks">The rank groups choice for downloads from deadlock-api.com, kept as it is.</param>
    public MatchDownloadViewModel(IModalService modals, IReadOnlyList<MatchSegment> stored, SnapshotPlan shared, bool includeRanks,
        bool keepUpToDate, Action<MatchDownloadChoice> download)
        : this(modals, stored, shared, shared, includeRanks, keepUpToDate, download)
    {
        IsShared = true;
        Intro = "Item win rates from real matches on deadlock-api.com, shown beside each recommendation as a second opinion. "
                + "They come ready-made from this app's shared download, with the rank groups the Match tab's filters use, "
                + $"last fetched from deadlock-api.com {MatchStatsMath.Age(shared.Now - shared.FetchedAt)}.";
        SharedDetail = shared.HasWork ? $"{SharedTime} · {MatchFetchEstimate.DescribeBytes(shared.Bytes)}" : "nothing to download";
        Describe(_ => $"takes {SharedTime} and uses");
    }

    private MatchDownloadViewModel(IModalService modals, IReadOnlyList<MatchSegment> stored, MatchDownloadPlan everyMatch, MatchDownloadPlan withRanks,
        bool includeRanks, bool keepUpToDate, Action<MatchDownloadChoice> download)
    {
        _everyMatch = everyMatch;
        _withRanks = withRanks;
        Stored = stored.Select(segment => new StatusFact($"Patch {segment.Patch.Label}", StoredText(segment))).ToList();
        StoredSegments = stored;
        IncludeRanks = includeRanks;
        KeepUpToDate = keepUpToDate;

        DownloadCommand = ReactiveCommand.Create(() =>
        {
            modals.CloseModal();
            download(new MatchDownloadChoice(Chosen, IncludeRanks, KeepUpToDate));
        }, this.WhenAnyValue(vm => vm.IncludeRanks).Select(_ => Chosen.HasWork));
        CancelCommand = ReactiveCommand.Create(modals.CloseModal);
    }

    private IReadOnlyList<MatchSegment> StoredSegments { get; }

    /// <summary>Keeps the steps and summary in line with the choice; <paramref name="cost"/> fills in "Downloading 1 patch …".</summary>
    private void Describe(Func<MatchDownloadPlan, string> cost) =>
        this.WhenAnyValue(vm => vm.IncludeRanks)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(EveryMatchOnly));
                var plan = Chosen;
                Steps = plan.Describe();
                var downloading = Steps.Count(step => step.Downloads);
                Summary = downloading == 0
                    ? "Everything is already up to date, so there's nothing to download."
                    : $"Downloading {downloading} patch{(downloading == 1 ? "" : "es")} {cost(plan)} "
                      + $"about {MatchFetchEstimate.DescribeBytes(plan.DiskBytes(StoredSegments))} of disk space.";
            })
            .DisposeWith(Disposables);

    public string Title => "Download match data";

    public string Intro { get; } = "";

    /// <summary>From the shared download: no choice to make, the rank groups come with it.</summary>
    public bool IsShared { get; }

    /// <summary>"a few seconds · 0.6 MB".</summary>
    public string SharedDetail { get; } = "";

    /// <summary>One line per patch stored now, newest first; empty before the first download.</summary>
    public IReadOnlyList<StatusFact> Stored { get; }

    public bool HasStored => Stored.Count > 0;

    /// <summary>Take the rank groups too.</summary>
    [Reactive] public bool IncludeRanks { get; set; }

    public bool EveryMatchOnly
    {
        get => !IncludeRanks;
        set => IncludeRanks = !value;
    }

    /// <summary>Refresh the match data in the background from now on, as <see cref="AutoUpdateInfo"/> says.</summary>
    [Reactive] public bool KeepUpToDate { get; set; }

    /// <summary>"about 40 s · 1.2 MB".</summary>
    public string EveryMatchDetail { get; } = "";

    public string WithRanksDetail { get; } = "";

    /// <summary>What happens to each patch kept, with the choice made.</summary>
    [Reactive] public IReadOnlyList<PlanStep> Steps { get; private set; } = [];

    [Reactive] public string Summary { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    private MatchDownloadPlan Chosen => IncludeRanks ? _withRanks : _everyMatch;

    private static string Cost(MatchFetchPlan plan, MatchFetchEstimate estimate) =>
        plan.Calls == 0
            ? "nothing to download"
            : $"{MatchFetchEstimate.DescribeTime(estimate.Time(plan))} · {MatchFetchEstimate.DescribeBytes(estimate.Bytes(plan))}";

    /// <summary>"2 days so far · all matches", "13 days · finished · all matches + rank groups".</summary>
    private static string StoredText(MatchSegment segment) =>
        MatchStatsMath.PatchSpan(segment.From, segment.Until, segment.Ended)
        + (segment.Complete ? " · finished" : "")
        + (segment.HasRanks ? " · all matches + rank groups" : " · all matches");
}
