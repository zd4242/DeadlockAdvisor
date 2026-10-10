using Avalonia.Controls.Primitives;

namespace DeadlockAdvisor.Behaviors;

/// <summary>
/// Tooltips, flyouts, menus and drop-down lists open in a popup of their own, outside the transform
/// that zooms the window. A popup that inherits its placement target's transform takes the zoom with
/// it, and sits where it should because it is measured at its zoomed size.
/// <para>
/// A style can't set this: the tooltip and flyout popups are made in code, so the property is set
/// when a popup gets its content, which is before it opens.
/// </para>
/// </summary>
public static class PopupsFollowZoom
{
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
            return;
        _enabled = true;
        Popup.ChildProperty.Changed.AddClassHandler<Popup>((popup, _) => popup.InheritsTransform = true);
    }
}
