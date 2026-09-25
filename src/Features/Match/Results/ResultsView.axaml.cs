using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;

namespace DeadlockAdvisor.Features.Match.Results;

public partial class ResultsView : ReactiveUserControl<ResultsViewModel>
{
    public ResultsView()
    {
        InitializeComponent();
        List.AddHandler(PointerPressedEvent, OnListPressed, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>A left click on a row selects it; on a tier header, folds or unfolds that tier.</summary>
    private void OnListPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is not Visual source)
            return;

        var owner = source.GetSelfAndVisualAncestors().OfType<Control>()
            .FirstOrDefault(control => control.DataContext is ResultRowViewModel or TierHeaderViewModel);
        switch (owner?.DataContext)
        {
            case ResultRowViewModel row:
                ViewModel.Select(row);
                break;
            case TierHeaderViewModel header:
                ViewModel.ToggleTier(header.Tier);
                break;
        }
    }
}
