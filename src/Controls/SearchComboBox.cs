using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeadlockAdvisor.Core;

namespace DeadlockAdvisor.Controls;

/// <summary>
/// A combo box with a search box at the top of its dropdown. Typing narrows the list with a fuzzy match and selects
/// the best one; Up/Down step through what's left, and Enter or Escape closes it. Typing on the closed box opens it
/// with the search started. Entries match on their <see cref="object.ToString"/>, as ComboBox's own text search does.
/// </summary>
public class SearchComboBox : NoWheelComboBox
{
    private readonly SearchBox _search = new() { Watermark = "Type to search...", Margin = new Thickness(4, 4, 4, 2) };

    public SearchComboBox()
    {
        // Every entry realized, so the search can hide any of them.
        ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel());

        _search.TextChanged += (_, _) => Filter();
        _search.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);

        DropDownOpened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            Filter();
            _search.Focus();
            _search.CaretIndex = _search.Text?.Length ?? 0;
        });
        DropDownClosed += (_, _) =>
        {
            // Back to the box from the search, for the next Tab or keystroke, but not away from a click elsewhere.
            if (_search.IsKeyboardFocusWithin)
                Focus();
            _search.Text = "";
        };
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        (_search.Parent as Panel)?.Children.Remove(_search);
        // The Fluent dropdown is a popup holding a border around the scrolling list: the search goes above the
        // list, inside the border.
        if (e.NameScope.Find<Popup>("PART_Popup") is not { Child: Decorator { Child: { } list } frame })
            return;
        frame.Child = null;
        DockPanel.SetDock(_search, Dock.Top);
        frame.Child = new DockPanel { Children = { _search, list } };
    }

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        // ComboBox focuses each entry it selects while open, which would take the typing away from the search.
        container.Focusable = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Keys typed into the search bubble up here from the popup: they're the search's, not the combo box's,
        // which would take Space as picking the focused entry. Marking them handled in the search instead would
        // also stop Windows from delivering the characters they type.
        if (_search.IsKeyboardFocusWithin && e.Key != Key.Tab)
            return;
        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        // Typing on the closed box starts a search, rather than ComboBox's jump to the first entry with that prefix.
        if (IsDropDownOpen || string.IsNullOrWhiteSpace(e.Text))
        {
            base.OnTextInput(e);
            return;
        }
        _search.Text = e.Text;
        IsDropDownOpen = true;
        e.Handled = true;
    }

    /// <summary>Hide the entries the search doesn't match and select the best match; an empty search shows every entry.</summary>
    private void Filter()
    {
        var entries = Items.ToList();
        var ranked = FuzzyMatch.Filter(entries, _search.Text, entry => entry?.ToString() ?? "");
        var shown = ranked.ToHashSet();
        for (var index = 0; index < entries.Count; index++)
        {
            if (ContainerFromIndex(index) is { } container)
                container.IsVisible = shown.Contains(entries[index]);
        }
        if (!string.IsNullOrWhiteSpace(_search.Text) && ranked.Count > 0)
            SelectedItem = ranked[0];
        if (SelectedIndex >= 0)
            ScrollIntoView(SelectedIndex);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Step(1);
                e.Handled = true;
                break;
            case Key.Up:
                Step(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                IsDropDownOpen = false;
                e.Handled = true;
                break;
            // With text in the box, Escape clears it first.
            case Key.Escape when string.IsNullOrEmpty(_search.Text):
                IsDropDownOpen = false;
                e.Handled = true;
                break;
        }
    }

    /// <summary>To the next or previous entry the search shows, stopping at the ends.</summary>
    private void Step(int direction)
    {
        for (var index = SelectedIndex + direction; index >= 0 && index < ItemCount; index += direction)
        {
            if (ContainerFromIndex(index) is not { IsVisible: false })
            {
                SelectedIndex = index;
                ScrollIntoView(index);
                return;
            }
        }
    }
}
