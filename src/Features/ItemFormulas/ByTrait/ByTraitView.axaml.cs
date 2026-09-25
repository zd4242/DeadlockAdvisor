using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using ReactiveUI;

namespace DeadlockAdvisor.Features.ItemFormulas.ByTrait;

public partial class ByTraitView : ReactiveUserControl<ByTraitViewModel>
{
    public ByTraitView()
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
        Grid.AddHandler(CoefficientGrid.CoefficientEditedEvent, (_, e) => ViewModel?.SetCoefficient(e.Row, e.Value));
        Grid.AddHandler(CoefficientGrid.SortRequestedEvent, (_, e) => ViewModel?.SortCommand.Execute(e.Column).Subscribe());
        Scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty && ViewModel is not null)
                ViewModel.PageRows = Math.Max(1, (int)((Scroller.Viewport.Height - CoefficientGrid.HeaderHeight) / CoefficientGrid.RowHeight));
        };

        this.WhenActivated(disposables =>
        {
            ViewModel!.ViewInteraction
                .Subscribe(action =>
                {
                    if (action == ByTraitViewModel.FocusSearchAction)
                    {
                        SearchBox.Focus();
                        SearchBox.SelectAll();
                    }
                })
                .DisposeWith(disposables);
        });
    }

    /// <summary>Put the keyboard on the grid, ready for typing coefficients.</summary>
    public void FocusGrid() => Grid.Focus();
}
