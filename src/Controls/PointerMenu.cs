namespace DeadlockAdvisor.Controls;

/// <summary>
/// Menus opened from code at the pointer, one at a time. The controls that open them handle the press
/// themselves, so it never reaches the light dismiss of a menu already open over the main window.
/// </summary>
public static class PointerMenu
{
    private static ContextMenu? _open;

    public static void Show(Control target, IEnumerable<Control> items)
    {
        _open?.Close();

        var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
        menu.Closed += (_, _) =>
        {
            if (_open == menu)
                _open = null;
        };
        _open = menu;
        menu.Open(target);
    }
}
