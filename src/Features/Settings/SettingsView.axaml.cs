using System.Reactive.Linq;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace DeadlockAdvisor.Features.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        PointerPressed += OnPointerPressed;
    }

    /// <summary>Take focus as the page opens: the page it covers may have had it, and keys would go on landing there.</summary>
    public void FocusCategories() =>
        Dispatcher.UIThread.Post(() => (Nav.ContainerFromItem(Nav.SelectedItem!) ?? Nav).Focus(), DispatcherPriority.Loaded);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            Close(e);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsXButton1Pressed)
            Close(e);
    }

    private void Close(RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.CloseCommand.Execute().Subscribe();
        e.Handled = true;
    }
}
