using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;

namespace DeadlockAdvisor.Features.Match.Import;

public partial class ImportMatchView : ReactiveUserControl<ImportMatchViewModel>
{
    public ImportMatchView()
    {
        InitializeComponent();

        MatchId.AddHandler(KeyDownEvent, OnMatchIdKeyDown, RoutingStrategies.Tunnel);
        AttachedToVisualTree += async (_, _) =>
        {
            // Small delay so the modal has rendered before taking focus.
            await Task.Delay(50);
            MatchId.Focus();
        };
    }

    private void OnMatchIdKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is null)
            return;
        ViewModel.Submit();
        e.Handled = true;
    }
}
