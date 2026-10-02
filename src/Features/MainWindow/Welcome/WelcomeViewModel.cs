using System.Reactive;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow.Welcome;

/// <param name="MatchData">The match data plan to download; null for none.</param>
public sealed record WelcomeChoice(bool Art, MatchFetchPlan? MatchData, bool Ranks, bool KeepUpToDate);

/// <summary>
/// A first run's offer, in one go: the art the pages show and Detect reads, and the match data the
/// recommendations take a second opinion from, each with what it costs, and whether to keep the match
/// data up to date from then on.
/// </summary>
public sealed class WelcomeViewModel : ViewModelBase
{
    public const string ArtSize = "about 22 MB";

    private readonly MatchFetchPlan? _everyMatch;
    private readonly MatchFetchPlan? _withRanks;

    /// <param name="everyMatch">Null when the patch list couldn't be fetched, so there's no match data to offer.</param>
    public WelcomeViewModel(IModalService modals, MatchFetchPlan? everyMatch, MatchFetchPlan? withRanks, MatchFetchEstimate estimate,
        bool keepUpToDate, Action<WelcomeChoice> start)
    {
        _everyMatch = everyMatch;
        _withRanks = withRanks;
        CanDownloadMatchData = everyMatch is not null && withRanks is not null;
        MatchData = CanDownloadMatchData;
        KeepUpToDate = keepUpToDate;
        if (everyMatch is not null && withRanks is not null)
        {
            MatchDataDetail = $"{MatchFetchEstimate.DescribeTime(estimate.Time(everyMatch))} · {MatchFetchEstimate.DescribeBytes(estimate.Bytes(everyMatch))}";
            var extra = TimeSpan.FromTicks(estimate.Time(withRanks).Ticks - estimate.Time(everyMatch).Ticks);
            RanksDetail = $"{MatchFetchEstimate.DescribeTime(extra)} more · {MatchFetchEstimate.DescribeBytes(estimate.Bytes(withRanks) - estimate.Bytes(everyMatch))}";
        }

        this.WhenAnyValue(vm => vm.MatchData)
            .Where(on => !on)
            .Subscribe(_ => Ranks = false);

        StartCommand = ReactiveCommand.Create(() =>
            {
                modals.CloseModal();
                start(new WelcomeChoice(Art, MatchData ? Ranks ? _withRanks : _everyMatch : null, Ranks, KeepUpToDate));
            },
            this.WhenAnyValue(vm => vm.Art, vm => vm.MatchData, (art, matchData) => art || matchData));
        NotNowCommand = ReactiveCommand.Create(modals.CloseModal);
    }

    public string Title => "Welcome to Deadlock Item Advisor";

    public string Intro =>
        "It recommends items for the heroes in your match. Two downloads make it better, both from deadlock-api.com "
        + "and both in the background, so you can start right away.";

    [Reactive] public bool Art { get; set; } = true;

    public string ArtText => $"Hero portraits, item icons and the top-bar art Detect reads ({ArtSize})";

    /// <summary>The patch list came back, so there's a plan to offer.</summary>
    public bool CanDownloadMatchData { get; }

    [Reactive] public bool MatchData { get; set; }

    /// <summary>"about 1 min · 2.4 MB".</summary>
    public string MatchDataDetail { get; } = "";

    [Reactive] public bool Ranks { get; set; }

    /// <summary>"about 6 min more · 11.0 MB".</summary>
    public string RanksDetail { get; } = "";

    [Reactive] public bool KeepUpToDate { get; set; }

    public string Offline => "deadlock-api.com didn't answer, so the match data waits: Data → Download Match Data… any time.";

    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> NotNowCommand { get; }
}
