using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Shared.Modals.Progress;

/// <summary>
/// A long network job's progress: what it's doing, how far along, and a Cancel that stops it before
/// its next step (the call in flight is dropped). Reports arrive on the UI thread.
/// </summary>
public class ProgressModalViewModel : ViewModelBase, IProgress<FetchProgress>
{
    private readonly CancellationTokenSource _cancel = new();

    public ProgressModalViewModel(string title, string text, bool canCancel = true)
    {
        Title = title;
        Text = text;
        CanCancel = canCancel;
        CancelCommand = ReactiveCommand.Create(() =>
        {
            Text = "Cancelling…";
            CanCancel = false;
            _cancel.Cancel();
        }, this.WhenAnyValue(vm => vm.CanCancel));
    }

    public string Title { get; }
    [Reactive] public string Text { get; private set; }
    [Reactive] public string Detail { get; private set; } = "";
    [Reactive] public double Done { get; private set; }
    [Reactive] public double Total { get; private set; } = 1;
    [Reactive] public bool IsIndeterminate { get; private set; } = true;
    [Reactive] public bool CanCancel { get; private set; }

    public CancellationToken Token => _cancel.Token;

    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public void Report(FetchProgress value)
    {
        Total = Math.Max(1, value.Total);
        Done = value.Done;
        Detail = value.Text;
        IsIndeterminate = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _cancel.Dispose();
        base.Dispose(disposing);
    }
}
