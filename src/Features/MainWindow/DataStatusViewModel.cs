using System.Reactive;
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
/// The status bar's match data chip, and the card it opens: which patch and matches the data comes
/// from and how old it is, what each kind of lift kept, a newer patch if one is out, and while the
/// model editors are shown, how much of the model is filled in.
/// </summary>
public class DataStatusViewModel : ViewModelBase
{
    public const string NoDataText =
        "Real-match win rates add a second opinion to the recommendations. Fetching them takes a few minutes, in the background.";

    private readonly IDataService _data;
    private readonly DataMenuViewModel _dataMenu;

    public DataStatusViewModel(IDataService data, DataMenuViewModel dataMenu, ISettingsService settings)
    {
        _data = data;
        _dataMenu = dataMenu;
        FetchCommand = dataMenu.FetchMatchStatsCommand;

        data.StoreReplaced.Merge(data.ScoresChanged).Subscribe(_ => Refresh()).DisposeWith(Disposables);
        dataMenu.WhenAnyValue(menu => menu.NewerPatch).Skip(1).Subscribe(_ => Refresh()).DisposeWith(Disposables);
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

    /// <summary>The chip's text: the patch and age of the match data, or that there's none.</summary>
    [Reactive] public string Label { get; private set; } = "";

    [Reactive] public bool HasData { get; private set; }

    /// <summary>A newer patch is out than the match data was fetched under, so it wants fetching again.</summary>
    [Reactive] public bool IsOutdated { get; private set; }

    /// <summary>What the newer patch means for the numbers; null while the data is current.</summary>
    [Reactive] public string? Warning { get; private set; }

    [Reactive] public IReadOnlyList<StatusFact> Facts { get; private set; } = [];

    /// <summary>What each kind of lift kept from the matches, or why it was left out.</summary>
    [Reactive] public IReadOnlyList<string> Families { get; private set; } = [];

    [Reactive] public string FetchText { get; private set; } = "";

    /// <summary>How much of the model is filled in, which only matters to someone filling it in.</summary>
    [Reactive] public bool ShowsCoverage { get; private set; }

    [Reactive] public IReadOnlyList<StatusFact> Coverage { get; private set; } = [];

    public ReactiveCommand<Unit, Unit> FetchCommand { get; }

    /// <summary>Recompute everything, as the card does on opening so the age is current.</summary>
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
            Label = IsOutdated ? $"Match data · patch {patch} · {_dataMenu.NewerPatch!.Label} is out" : $"Match data · patch {patch} · {age}";
            Warning = IsOutdated
                ? $"Patch {_dataMenu.NewerPatch!.Label} is out since these were fetched. Fetch again for numbers that match the game."
                : null;
            Facts =
            [
                new("Patch", patch),
                new("Matches", MatchStatsMath.RankLabel(meta) is { } rank ? $"Ranked only, {rank}" : "Every match, ranked or not"),
                new("Fetched", age),
            ];
            Families = MatchStatsMath.FamilyLines(meta);
            FetchText = "Fetch again";
        }
        else
        {
            Label = "No match data";
            Warning = null;
            Facts = [];
            Families = [];
            FetchText = "Fetch Match Stats";
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
