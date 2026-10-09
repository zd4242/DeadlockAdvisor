using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Input;
using DeadlockAdvisor.Core;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow.Updates;

/// <summary>How one thing that stays current, or all of them together, stands.</summary>
public enum UpdateState
{
    UpToDate,

    /// <summary>Current as far as anyone knows, but no check has ever got an answer.</summary>
    NotChecked,

    Checking,

    /// <summary>A download is under way.</summary>
    Updating,

    /// <summary>Something newer is out, or an offer waits for a click.</summary>
    Available,

    Offline,

    /// <summary>Turned off, or not downloaded at all: it says nothing about whether anything is current.</summary>
    Off,

    /// <summary>The last download ended in an error.</summary>
    Failed,
}

public enum UpdateSource
{
    App,
    Formulas,
    MatchData,
    Art,
}

/// <summary>A smaller action beside a row's main one: "What's new", "Skip this version".</summary>
public sealed record UpdateLink(string Text, ICommand Command, string? Tip = null);

/// <summary>What a row shows, worked out from the services' own state each time something they hold changes.</summary>
internal sealed record UpdateRowInfo(
    UpdateState State,
    string Summary,
    string Headline,
    string? ActionText = null,
    ICommand? Action = null,
    string? ActionTip = null,
    IReadOnlyList<UpdateLink>? Links = null,
    double? Percent = null);

/// <summary>A <see cref="UpdateState"/> for a view to colour by, in the flags a style can select on.</summary>
public abstract class UpdateStatusViewModel : ViewModelBase
{
    protected UpdateStatusViewModel()
    {
        this.WhenAnyValue(vm => vm.State)
            .Skip(1)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(IsGood));
                this.RaisePropertyChanged(nameof(IsWorking));
                this.RaisePropertyChanged(nameof(IsAttention));
                this.RaisePropertyChanged(nameof(IsWarning));
                this.RaisePropertyChanged(nameof(IsProblem));
                this.RaisePropertyChanged(nameof(IsUpdating));
            })
            .DisposeWith(Disposables);
    }

    [Reactive] public UpdateState State { get; protected set; }

    public bool IsGood => State == UpdateState.UpToDate;
    public bool IsWorking => State is UpdateState.Checking or UpdateState.Updating;
    public bool IsUpdating => State == UpdateState.Updating;
    public bool IsAttention => State == UpdateState.Available;
    public bool IsWarning => State == UpdateState.Offline;
    public bool IsProblem => State == UpdateState.Failed;
}

/// <summary>One line of the Updates flyout: the app, the formulas, the match data or the art.</summary>
public sealed class UpdateRowViewModel : UpdateStatusViewModel
{
    /// <param name="details">More about it, opened by the row's Details; null when there's nothing more to say.</param>
    public UpdateRowViewModel(UpdateSource source, string title, ViewModelBase? details = null)
    {
        Source = source;
        Title = title;
        Details = details;
        ToggleDetailsCommand = ReactiveCommand.Create(() =>
        {
            IsExpanded = !IsExpanded;
        });
    }

    public UpdateSource Source { get; }

    /// <summary>"App", "Formulas", "Match data", "Art".</summary>
    public string Title { get; }

    public ViewModelBase? Details { get; }
    public bool HasDetails => Details is not null;

    [Reactive] public bool IsExpanded { get; private set; }

    /// <summary>"patch 10-07 · up to date · checked 2h ago".</summary>
    [Reactive] public string Summary { get; private set; } = "";

    /// <summary>What the status bar's chip says when this is the one thing worth saying.</summary>
    [Reactive] public string Headline { get; private set; } = "";

    [Reactive] public string? ActionText { get; private set; }
    [Reactive] public ICommand? Action { get; private set; }
    [Reactive] public string? ActionTip { get; private set; }
    [Reactive] public IReadOnlyList<UpdateLink> Links { get; private set; } = [];

    /// <summary>How far a download has got, 0–100; null while it's under way with nothing to count yet.</summary>
    [Reactive] public double? Percent { get; private set; }

    public ReactiveCommand<Unit, Unit> ToggleDetailsCommand { get; }

    internal void Apply(UpdateRowInfo info)
    {
        State = info.State;
        Summary = info.Summary;
        Headline = info.Headline;
        ActionText = info.ActionText;
        Action = info.Action;
        ActionTip = info.ActionTip;
        Percent = info.Percent;
        var links = info.Links ?? [];
        // A new list on every progress report would rebuild the links under the pointer.
        if (!Links.SequenceEqual(links))
            Links = links;
    }
}
