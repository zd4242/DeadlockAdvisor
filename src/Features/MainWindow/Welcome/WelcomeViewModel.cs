using System.Reactive;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.MainWindow.MatchDownload;
using DeadlockAdvisor.Features.Settings.Data;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow.Welcome;

/// <param name="MatchData">The match results plan to download; null for none.</param>
/// <param name="Ranks">The rank groups choice, for downloads from deadlock-api.com; the shared download always has them.</param>
/// <param name="Mode">How the match results and the advisor rating keep up to date from then on.</param>
public sealed record WelcomeChoice(bool Art, MatchDownloadPlan? MatchData, bool Ranks, UpdateMode Mode);

/// <summary>
/// A first run's offer, in one go: the art the pages show and Detect reads, and the match results the
/// recommendations take a second opinion from, each with what it costs, and how the match results and the
/// advisor rating keep up to date from then on (Automatic, Tell me or Off).
/// </summary>
public sealed class WelcomeViewModel : ViewModelBase
{
    public const string ArtSize = "about 23 MB";

    private readonly MatchDownloadPlan? _everyMatch;
    private readonly MatchDownloadPlan? _withRanks;

    /// <param name="shared">The shared download's plan, which comes with the rank groups; null when it isn't available.</param>
    /// <param name="everyMatch">
    /// Without <paramref name="shared"/>, the download from deadlock-api.com. Null when the patch list couldn't be
    /// fetched either, so there's no match results to offer.
    /// </param>
    /// <param name="mode">How things keep up to date now, which the selector starts on.</param>
    public WelcomeViewModel(IModalService modals, SnapshotPlan? shared, MatchFetchPlan? everyMatch, MatchFetchPlan? withRanks,
        MatchFetchEstimate estimate, bool includeRanks, UpdateMode mode, Action<WelcomeChoice> start)
    {
        _everyMatch = shared ?? (MatchDownloadPlan?)everyMatch;
        _withRanks = shared ?? (MatchDownloadPlan?)withRanks;
        IsShared = shared is not null;
        CanDownloadMatchData = _everyMatch is not null && _withRanks is not null;
        MatchData = CanDownloadMatchData;
        Mode = Modes.Single(option => option.Mode == mode);
        if (shared is not null)
        {
            MatchDataDetail = $"{MatchDownloadViewModel.SharedTime} · {MatchFetchEstimate.DescribeBytes(shared.Bytes)} · with the rank groups";
            Ranks = includeRanks;
        }
        else if (everyMatch is not null && withRanks is not null)
        {
            MatchDataDetail = $"{MatchFetchEstimate.DescribeTime(estimate.Time(everyMatch))} · {MatchFetchEstimate.DescribeBytes(estimate.Bytes(everyMatch))}";
            var extra = TimeSpan.FromTicks(estimate.Time(withRanks).Ticks - estimate.Time(everyMatch).Ticks);
            RanksDetail = $"{MatchFetchEstimate.DescribeTime(extra)} more · {MatchFetchEstimate.DescribeBytes(estimate.Bytes(withRanks) - estimate.Bytes(everyMatch))}";
        }

        this.WhenAnyValue(vm => vm.MatchData)
            .Where(on => !on && !IsShared)
            .Subscribe(_ => Ranks = false);

        StartCommand = ReactiveCommand.Create(() =>
            {
                modals.CloseModal();
                start(new WelcomeChoice(Art, MatchData ? Ranks ? _withRanks : _everyMatch : null, Ranks, Mode.Mode));
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

    /// <summary>From the shared download: the rank groups come with it, so there's no choice about them.</summary>
    public bool IsShared { get; }

    /// <summary>The shared download or deadlock-api.com's patch list came back, so there's a plan to offer.</summary>
    public bool CanDownloadMatchData { get; }

    [Reactive] public bool MatchData { get; set; }

    /// <summary>"about 1 min · 2.4 MB".</summary>
    public string MatchDataDetail { get; } = "";

    [Reactive] public bool Ranks { get; set; }

    /// <summary>"about 6 min more · 11.0 MB".</summary>
    public string RanksDetail { get; } = "";

    /// <summary>How the match results and the advisor rating keep up to date; the same three choices as Settings → Data.</summary>
    public IReadOnlyList<UpdateModeOption> Modes { get; } =
    [
        new(UpdateMode.Automatic, "Automatic",
            "Keeps both current in the background, so the recommendations follow each patch. Recommended."),
        new(UpdateMode.TellMe, "Tell me",
            "Says in the status bar when something newer is out. Nothing changes until you ask."),
        new(UpdateMode.Off, "Off",
            "Never checks by itself. Data → Check for Updates does it when you want."),
    ];

    [Reactive] public UpdateModeOption Mode { get; set; }

    public string Offline => "Neither the shared download nor deadlock-api.com answered, so the match results wait: Data → Check for Updates any time.";

    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> NotNowCommand { get; }
}
