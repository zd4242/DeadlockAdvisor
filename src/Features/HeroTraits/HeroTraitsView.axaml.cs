using System.Reactive.Disposables;
using Avalonia.Input;
using Avalonia.ReactiveUI;
using DeadlockAdvisor.Controls.Grids;
using ReactiveUI;

namespace DeadlockAdvisor.Features.HeroTraits;

public partial class HeroTraitsView : ReactiveUserControl<HeroTraitsViewModel>
{
    public HeroTraitsView()
    {
        InitializeComponent();

        // Only keys aimed at the grid itself: its floating spin box handles its own.
        Grid.KeyDown += (_, e) =>
        {
            if (e.Source == Grid && ViewModel?.HandleKey(e.Key, e.KeyModifiers) == true)
                e.Handled = true;
        };
        Grid.TextInput += (_, e) =>
        {
            if (e.Source == Grid && e.Text is { } text && ViewModel?.HandleText(text) == true)
                e.Handled = true;
        };
        Grid.AddHandler(TraitGrid.CellEditedEvent, (_, e) => ViewModel?.SetValue(e.Row, e.Column, e.Value));
        Grid.AddHandler(ScrollingGrid.SortRequestedEvent, (_, e) => ViewModel?.SortCommand.Execute(e.Column).Subscribe());
        Scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty && ViewModel is not null)
                ViewModel.PageRows = Math.Max(1, (int)((Scroller.Viewport.Height - Grid.HeaderHeight) / TraitGrid.RowHeight));
        };

        this.WhenActivated(disposables =>
        {
            ViewModel!.ViewInteraction
                .Subscribe(action =>
                {
                    if (action == HeroTraitsViewModel.FocusSearchAction)
                    {
                        SearchBox.Focus();
                        SearchBox.SelectAll();
                    }
                })
                .DisposeWith(disposables);
        });
    }

    /// <summary>Put the keyboard on the grid, ready for typing numbers.</summary>
    public void FocusGrid() => Grid.Focus();
}
