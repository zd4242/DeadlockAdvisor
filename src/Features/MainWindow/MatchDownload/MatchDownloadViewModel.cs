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
        "The rank groups let the Match tab's filters lean the numbers toward the ranks you play.\n"
        + "They're five times the calls: every match once more for each pair of ranks.";

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
                Steps = plan.Describe(stored).Select(step => new StatusFact(step.Patch, step.Text)).ToList();
                Summary = plan.Calls == 0
                    ? "Every patch's counts are complete: nothing to download."
                    : $"{plan.Calls} calls, {MatchFetchEstimate.DescribeTime(estimate.Time(plan))}, in the background. "
                      + $"About {MatchFetchEstimate.DescribeBytes(plan.DiskBytes(stored))} on disk once done.";
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
        "Item win rates from real matches on deadlock-api.com: a second opinion beside each recommendation. "
        + "Finished patches are kept, so this only fetches what has changed.";

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
    [Reactive] public IReadOnlyList<StatusFact> Steps { get; private set; } = [];

    [Reactive] public string Summary { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    private MatchFetchPlan Chosen => IncludeRanks ? _withRanks : _everyMatch;

    private string Cost(MatchFetchPlan plan) =>
        plan.Calls == 0
            ? "nothing to fetch"
            : $"{MatchFetchEstimate.DescribeTime(_estimate.Time(plan))} · {MatchFetchEstimate.DescribeBytes(_estimate.Bytes(plan))}";

    /// <summary>"2 days so far · every match", "13 days · complete · with rank groups".</summary>
    private static string StoredText(MatchSegment segment) =>
        MatchStatsMath.PatchSpan(segment.From, segment.Until, segment.Ended)
        + (segment.Complete ? " · complete" : "")
        + (segment.HasRanks ? " · with rank groups" : " · every match");
}
