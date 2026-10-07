using System.Reactive.Disposables;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;
using ReactiveUI;

namespace DeadlockAdvisor.Features.Match;

public partial class MatchView : ReactiveUserControl<MatchViewModel>
{
    public MatchView()
    {
        InitializeComponent();

        // The box is only there while it's wanted: Escape puts it away, with whatever it held, and so does leaving it empty.
        // Tunnelled, to get ahead of the box's own Escape, which only empties it.
        ItemSearchBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseItemSearch();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        ItemSearchBox.LostFocus += (_, _) => CloseEmptyItemSearch();

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

            // Focus only moves when something focusable is clicked, so an empty box is also put away by a click anywhere
            // else in the window (the toggle button handles its own). The window, because empty parts of the page don't hit-test.
            if (TopLevel.GetTopLevel(this) is { } window)
            {
                window.AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
                Disposable.Create(() => window.RemoveHandler(PointerPressedEvent, OnWindowPointerPressed)).DisposeWith(disposables);
            }
        });
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && !IsWithin(source, ItemSearchBox) && !IsWithin(source, ItemSearchToggle))
            CloseEmptyItemSearch();
    }

    private void FocusItemSearch()
    {
        // Opening the box has only just made it visible, and it can't take focus until it's laid out.
        UpdateLayout();
        ItemSearchBox.Focus();
        ItemSearchBox.SelectAll();
    }

    private static bool IsWithin(Visual source, Visual container) => source == container || container.IsVisualAncestorOf(source);

    private void CloseEmptyItemSearch()
    {
        if (string.IsNullOrEmpty(ItemSearchBox.Text))
            CloseItemSearch();
    }

    private void CloseItemSearch()
    {
        ViewModel?.Results.ResetSearch();
    }
}
