using System.Reactive.Disposables;
using Avalonia.Input;
using Avalonia.ReactiveUI;
using ReactiveUI;

namespace DeadlockAdvisor.Features.Match;

public partial class MatchView : ReactiveUserControl<MatchViewModel>
{
    public MatchView()
    {
        InitializeComponent();

        // The box is only there while it's wanted: Escape on an empty one, or leaving it empty, puts it away.
        ItemSearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && string.IsNullOrEmpty(ItemSearchBox.Text))
            {
                CloseItemSearch();
                e.Handled = true;
            }
        };
        ItemSearchBox.LostFocus += (_, _) =>
        {
            if (string.IsNullOrEmpty(ItemSearchBox.Text))
                CloseItemSearch();
        };

        // Escape that nothing inside the page used (a search box clearing itself, say) puts the hero picker away,
        // wherever focus sits in the page.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && ViewModel?.Board is { IsPickerOpen: true } board)
            {
                board.IsPickerOpen = false;
                e.Handled = true;
            }
        };

        this.WhenActivated(disposables =>
        {
            ViewModel!.ViewInteraction
                .Subscribe(action =>
                {
                    if (action == MatchViewModel.FocusItemSearchAction)
                        FocusItemSearch();
                })
                .DisposeWith(disposables);
        });
    }

    private void FocusItemSearch()
    {
        // Opening the box has only just made it visible, and it can't take focus until it's laid out.
        UpdateLayout();
        ItemSearchBox.Focus();
        ItemSearchBox.SelectAll();
    }

    private void CloseItemSearch()
    {
        if (ViewModel is { } match)
            match.Results.IsSearchOpen = false;
    }
}
