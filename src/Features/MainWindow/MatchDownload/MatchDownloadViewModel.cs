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

/// <summary>
/// Before a match data download: what's stored, what this one fetches and keeps, and whether to take
/// the rank groups too, each with about how long it takes and how much it brings.
/// </summary>
public sealed class MatchDownloadViewModel : ViewModelBase
{
    public const string RanksInfo =
        "Rank groups split the matches by rank, so the Match tab's filters can lean the numbers toward the ranks you play.\n"
        + "They take about five times as long to download.";

    private readonly MatchFetchPlan _everyMatch;
    private readonly MatchFetchPlan _withRanks;
    private readonly MatchFetchEstimate _estimate;

    /// <param name="download">Starts the chosen plan; told whether it takes the rank groups.</param>
    public MatchDownloadViewModel(IModalService modals, IReadOnlyList<MatchSegment> stored, MatchFetchPlan everyMatch, MatchFetchPlan withRanks,
        MatchFetchEstimate estimate, bool includeRanks, Action<MatchFetchPlan, bool> download)
    {
        _everyMatch = everyMatch;
        _withRanks = withRanks;
        _estimate = estimate;
        IncludeRanks = includeRanks;
        Stored = stored.Select(segment => new StatusFact($"Patch {segment.Patch.Label}", StoredText(segment))).ToList();
        EveryMatchDetail = Cost(everyMatch);
        WithRanksDetail = Cost(withRanks);

        this.WhenAnyValue(vm => vm.IncludeRanks)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(EveryMatchOnly));
                var plan = Chosen;
                Steps = plan.Describe();
                var downloading = Steps.Count(step => step.Downloads);
                Summary = downloading == 0
                    ? "Everything is already up to date, so there's nothing to download."
                    : $"Downloading {downloading} patch{(downloading == 1 ? "" : "es")} takes {MatchFetchEstimate.DescribeTime(estimate.Time(plan))}. "
                      + "It runs in the background, so you can keep using the app, "
                      + $"and uses about {MatchFetchEstimate.DescribeBytes(plan.DiskBytes(stored))} of disk space.";
            })
            .DisposeWith(Disposables);

        DownloadCommand = ReactiveCommand.Create(() =>
        {
            modals.CloseModal();
            download(Chosen, IncludeRanks);
        }, this.WhenAnyValue(vm => vm.IncludeRanks).Select(_ => Chosen.Calls > 0));
        CancelCommand = ReactiveCommand.Create(modals.CloseModal);
    }

    public string Title => "Download match data";

    public string Intro =>
        "Item win rates from real matches on deadlock-api.com, shown beside each recommendation as a second opinion. "
        + "Patches you already have in full are skipped, so only what's new is downloaded.";

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

    /// <summary>"about 40 s · 1.2 MB".</summary>
    public string EveryMatchDetail { get; }

    public string WithRanksDetail { get; }

    /// <summary>What happens to each patch kept, with the choice made.</summary>
    [Reactive] public IReadOnlyList<PlanStep> Steps { get; private set; } = [];

    [Reactive] public string Summary { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    private MatchFetchPlan Chosen => IncludeRanks ? _withRanks : _everyMatch;

    private string Cost(MatchFetchPlan plan) =>
        plan.Calls == 0
            ? "nothing to download"
            : $"{MatchFetchEstimate.DescribeTime(_estimate.Time(plan))} · {MatchFetchEstimate.DescribeBytes(_estimate.Bytes(plan))}";

    /// <summary>"2 days so far · all matches", "13 days · finished · all matches + rank groups".</summary>
    private static string StoredText(MatchSegment segment) =>
        MatchStatsMath.PatchSpan(segment.From, segment.Until, segment.Ended)
        + (segment.Complete ? " · finished" : "")
        + (segment.HasRanks ? " · all matches + rank groups" : " · all matches");
}
