using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow;

/// <summary>
/// The status bar's word that the app can't reach the internet. It shows only while that's so, and clicking
/// it checks again at once rather than at the next timed check.
/// </summary>
public sealed class ConnectionViewModel : ViewModelBase
{
    private readonly IArtService _art;

    public ConnectionViewModel(IConnectivityService connectivity, IArtService art)
    {
        _art = art;
        RetryCommand = ReactiveCommand.Create(connectivity.Retry, this.WhenAnyValue(vm => vm.IsChecking).Select(checking => !checking));
        connectivity.States
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(Show)
            .DisposeWith(Disposables);
    }

    [Reactive] public bool IsShown { get; private set; }

    /// <summary>A check the user asked for is under way.</summary>
    [Reactive] public bool IsChecking { get; private set; }

    [Reactive] public string Label { get; private set; } = "";

    [Reactive] public string ToolTipText { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> RetryCommand { get; }

    private void Show(ConnectivityState state)
    {
        IsShown = state != ConnectivityState.Online;
        IsChecking = state == ConnectivityState.Checking;
        Label = IsChecking ? "Checking…" : "Offline";
        ToolTipText = "No internet connection. Everything works from what's saved; downloads and updates resume when it's back.\nClick to check now."
                      + (_art.Count(ArtKind.Hero) == 0
                          ? "\n\nHero art hasn't been downloaded yet, so heroes show as their initials and Detect can't read the screen."
                          : "");
    }
}
