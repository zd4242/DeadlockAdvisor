using System.Reactive.Disposables;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using DeadlockAdvisor.Controls;
using ReactiveUI;

namespace DeadlockAdvisor.Features.Match.Board;

public partial class MatchBoardView : ReactiveUserControl<MatchBoardViewModel>
{
    public MatchBoardView()
    {
        InitializeComponent();

        AddHandler(HeroTile.ClickedEvent, (_, e) => ViewModel?.TileClicked(e.HeroId));
        AddHandler(HeroTile.DoubleClickedEvent, (_, e) => ViewModel?.TileDoubleClicked(e.HeroId));
        AddHandler(HeroTile.RightClickedEvent, (_, e) => RoleMenu.Show(ViewModel, e));

        // The search box keeps focus through the keyboard flow: Enter assigns, Up/Down move the pick.
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);

        this.WhenActivated(disposables =>
        {
            ViewModel!.ViewInteraction
                .Subscribe(action =>
                {
                    if (action == MatchBoardViewModel.FocusSearchAction)
                        FocusSearch();
                })
                .DisposeWith(disposables);
        });
    }

    public void FocusSearch()
    {
        // Opening the picker has only just made the box visible, and it can't take focus until it's laid out.
        UpdateLayout();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null)
            return;
        switch (e.Key)
        {
            case Key.Enter:
                ViewModel.AssignHighlighted();
                e.Handled = true;
                break;
            case Key.Down:
                ViewModel.MoveHighlight(1);
                e.Handled = true;
                break;
            case Key.Up:
                ViewModel.MoveHighlight(-1);
                e.Handled = true;
                break;
            // With text in the box, Escape clears it first.
            case Key.Escape when string.IsNullOrEmpty(SearchBox.Text):
                ViewModel.IsPickerOpen = false;
                e.Handled = true;
                break;
        }
    }
}
