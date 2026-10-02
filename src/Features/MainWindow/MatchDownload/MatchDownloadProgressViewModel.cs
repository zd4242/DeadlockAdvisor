using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow.MatchDownload;

public enum PhaseState
{
    Waiting,
    Running,
    Done,
}

/// <summary>One phase of a match data download, as the progress card lists it.</summary>
public sealed class DownloadPhaseViewModel : ReactiveObject
{
    public DownloadPhaseViewModel(FetchPhase phase)
    {
        Text = phase.Text;
        Total = phase.Calls;
    }

    /// <summary>"09-29 · every match".</summary>
    public string Text { get; }

    public int Total { get; }

    [Reactive] public PhaseState State { get; private set; }
    [Reactive] public bool IsWaiting { get; private set; }
    [Reactive] public bool IsRunning { get; private set; }
    [Reactive] public bool IsDone { get; private set; }
    [Reactive] public int Done { get; private set; }

    /// <summary>What it's asking about while it runs, "in use" once done, and its calls before it starts.</summary>
    [Reactive] public string Detail { get; private set; } = "";

    public void Update(PhaseState state, int done, string asking)
    {
        State = state;
        IsWaiting = state == PhaseState.Waiting;
        IsRunning = state == PhaseState.Running;
        IsDone = state == PhaseState.Done;
        Done = done;
        Detail = state switch
        {
            PhaseState.Done => "in use",
            PhaseState.Running => asking,
            _ => $"{Total} calls",
        };
    }
}

/// <summary>
/// The card a running match data download opens from its chip: every phase with how far it is, the calls
/// and bytes so far, and any wait the API asked for.
/// </summary>
public sealed class MatchDownloadProgressViewModel : ViewModelBase, IProgress<MatchFetchProgress>
{
    public MatchDownloadProgressViewModel(MatchFetchPlan plan)
    {
        Phases = plan.Phases.Select(phase => new DownloadPhaseViewModel(phase)).ToList();
        foreach (var phase in Phases)
            phase.Update(PhaseState.Waiting, 0, "");
        CallsText = $"0 of {Format.Thousands(plan.Calls)} calls";
    }

    public IReadOnlyList<DownloadPhaseViewModel> Phases { get; }

    /// <summary>"312 of 1,060 calls".</summary>
    [Reactive] public string CallsText { get; private set; }

    /// <summary>"6.1 MB received".</summary>
    [Reactive] public string BytesText { get; private set; } = "";

    /// <summary>"deadlock-api.com asked to slow down: waiting 30 s"; null while nothing holds it up.</summary>
    [Reactive] public string? WaitText { get; private set; }

    /// <summary>The last report's calls and bytes, for learning the pace once it's done.</summary>
    public int Done { get; private set; }
    public long Bytes { get; private set; }

    public void Report(MatchFetchProgress value)
    {
        // "09-29 · every match · Enemies: Haze": the phase row already says the first two.
        var asking = value.Text.Split(" · ").Last();
        for (var i = 0; i < Phases.Count; i++)
        {
            var phase = Phases[i];
            if (i < value.Phase || i == value.Phase && value.PhaseDone >= phase.Total)
                phase.Update(PhaseState.Done, phase.Total, "");
            else if (i == value.Phase)
                phase.Update(PhaseState.Running, value.PhaseDone, asking);
        }
        Done = value.Done;
        Bytes = value.Bytes;
        CallsText = $"{Format.Thousands(value.Done)} of {Format.Thousands(value.Total)} calls";
        BytesText = $"{MatchFetchEstimate.DescribeBytes(value.Bytes)} received";
        WaitText = value.Wait is { } wait ? $"{wait.Reason}: waiting {wait.Length.TotalSeconds:0} s" : null;
    }
}
