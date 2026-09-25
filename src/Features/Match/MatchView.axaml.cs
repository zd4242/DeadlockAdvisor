using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using DeadlockAdvisor.Features.Match.Board;
using ReactiveUI;

namespace DeadlockAdvisor.Features.Match;

public partial class MatchView : ReactiveUserControl<MatchViewModel>
{
    public MatchView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            ViewModel!.ViewInteraction
                .Subscribe(action =>
                {
                    if (action == MatchBoardViewModel.FocusSearchAction)
                        BoardView.FocusSearch();
                })
                .DisposeWith(disposables);
        });
    }
}
