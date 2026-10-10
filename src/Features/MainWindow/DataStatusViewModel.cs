using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow;

/// <summary>One line of the status card: what it is on the left, how much on the right.</summary>
public sealed record StatusFact(string Label, string Value);

/// <summary>
/// What the Updates flyout shows about the match data: which patch and matches it comes from and how
/// old it is, what each kind of lift kept, a newer patch if one is out, and while the model editors
/// are shown, how much of the model is filled in.
/// </summary>
public class DataStatusViewModel : ViewModelBase
{
    public const string NoDataText =
        "Real-match results add a second opinion to the recommendations. Fetching them takes seconds from the shared download, "
        + "or a few minutes from deadlock-api.com, in the background.";

    /// <summary>How recently a check must have found no newer patch for the chip to say the data is up to date.</summary>
    internal static readonly TimeSpan UpToDateWindow = TimeSpan.FromDays(3);

    private readonly IDataService _data;
    private readonly DataMenuViewModel _dataMenu;
    private readonly ISettingsService _settings;

    public DataStatusViewModel(IDataService data, DataMenuViewModel dataMenu, ISettingsService settings)
    {
        _data = data;
        _dataMenu = dataMenu;
        _settings = settings;

        data.StoreReplaced.Merge(data.ScoresChanged).Subscribe(_ => Refresh()).DisposeWith(Disposables);
        dataMenu.WhenAnyValue(menu => menu.NewerPatch).Skip(1).Subscribe(_ => Refresh()).DisposeWith(Disposables);
        settings.SettingsChanged
            .Select(s => s.MatchDataCheckedAt)
            .DistinctUntilChanged()
            .Subscribe(_ => Refresh())
            .DisposeWith(Disposables);
        settings.SettingsChanged
            .Select(s => s.ShowModelEditors)
            .DistinctUntilChanged()
            .Subscribe(show =>
            {
                ShowsCoverage = show;
                Refresh();
            })
            .DisposeWith(Disposables);
    }

    /// <summary>The patch of the match data and that it's current or how old it is, or that there's none: "patch 10-07 · up to date", "none yet".</summary>
    [Reactive] public string Summary { get; private set; } = "";

    [Reactive] public bool HasData { get; private set; }

    /// <summary>A newer patch is out than the match data was fetched under, so it wants fetching again.</summary>
    [Reactive] public bool IsOutdated { get; private set; }

    /// <summary>What the newer patch means for the numbers; null while the data is current.</summary>
    [Reactive] public string? Warning { get; private set; }

    [Reactive] public IReadOnlyList<StatusFact> Facts { get; private set; } = [];

    /// <summary>What each kind of lift kept from the matches, or why it was left out: for the editors only, like <see cref="Coverage"/>.</summary>
    [Reactive] public IReadOnlyList<string> Families { get; private set; } = [];

    /// <summary>How much of the model is filled in, which only matters to someone filling it in.</summary>
    [Reactive] public bool ShowsCoverage { get; private set; }

    [Reactive] public IReadOnlyList<StatusFact> Coverage { get; private set; } = [];

    /// <summary>A recent check found no newer patch. A finished patch is never fetched again, so the data's own age says nothing then.</summary>
    private bool IsCurrent() =>
        _dataMenu.NewerPatch is null
        && _settings.Current.MatchDataCheckedAt is { } checkedAt
        && DateTimeOffset.UtcNow - checkedAt < UpToDateWindow;

    /// <summary>Recompute everything, as the flyout does on opening so the age is current.</summary>
    public void Refresh()
    {
        var meta = _data.Store.MatchMeta;
        var fetched = MatchStatsMath.FetchedAt(meta);
        HasData = fetched is not null;
        IsOutdated = HasData && _dataMenu.NewerPatch is not null;

        if (fetched is { } at)
        {
            var patch = MatchStatsMath.PatchLabel(meta);
            var age = MatchStatsMath.Age(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - at);
            var current = IsCurrent() ? "up to date" : age;
            Summary = IsOutdated ? $"patch {patch} · {_dataMenu.NewerPatch!.Label} is out" : $"patch {patch} · {current}";
            Warning = IsOutdated
                ? $"Patch {_dataMenu.NewerPatch!.Label} is out since these were fetched. Download again for numbers that match the game."
                : null;
            Facts =
            [
                .. MatchStatsMath.PatchFacts(meta).Select(fact => new StatusFact(fact.Label, fact.Value)),
                new("Matches", MatchStatsMath.RankLabel(meta) is { } rank ? $"Leaning toward {rank}" : "Every match, ranked or not"),
                new("Fetched", age),
            ];
            var families = MatchStatsMath.FamilyLines(meta);
            if (MatchStatsMath.DriftLine(meta) is { } drift)
                families.Add(drift);
            Families = ShowsCoverage ? families : [];
        }
        else
        {
            Summary = "none yet";
            Warning = null;
            Facts = [];
            Families = [];
        }

        var coverage = _data.Store.Coverage();
        Coverage = ShowsCoverage
            ?
            [
                new("Hero traits rated", $"{coverage.ScoresFilled}/{coverage.ScoresTotal}"),
                new("Items tagged", $"{coverage.ItemsTagged}/{coverage.ItemsTotal}"),
                new("Formula rules", $"{coverage.Rules} + {coverage.DerivedRules} from stats"),
            ]
            : [];
    }
}
