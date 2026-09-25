using System.Globalization;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls.Grids;

/// <summary>
/// Base for the custom-drawn editor grids. The grid sits in a <see cref="ScrollViewer"/> at its full
/// size and repaints on every scroll, so it can pin its headers to the viewport's edges and only
/// draw the cells in view. Also hosts the spin box that a double click or F2 floats over one cell.
/// </summary>
public abstract class ScrollingGrid : Control
{
    private readonly NumericUpDown _editor;
    private ScrollViewer? _scroller;
    private IDisposable? _scrollSubscription;
    private Rect _editorRect;
    private Action<double>? _commit;
    private int _editorDecimals;

    protected ScrollingGrid()
    {
        Focusable = true;
        FocusAdorner = null;
        ClipToBounds = true;

        _editor = new NumericUpDown
        {
            IsVisible = false,
            ClipValueToMinMax = true,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            MinHeight = 0,
        };
        _editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        // Clicking away commits, like Qt's item editors; focus stays wherever the click put it.
        _editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsKeyboardFocusWithinProperty && !_editor.IsKeyboardFocusWithin)
                CloseEditor(commit: true, refocus: false);
        };
        VisualChildren.Add(_editor);
        LogicalChildren.Add(_editor);
    }

    public bool IsEditing => _commit is not null;

    /// <summary>How far the scroll viewer holding us is scrolled.</summary>
    protected Vector Offset => _scroller?.Offset ?? default;

    /// <summary>The part of us the scroll viewer shows.</summary>
    protected Size Viewport => _scroller?.Viewport ?? Bounds.Size;

    protected Rect ViewRect => new(new Point(Offset.X, Offset.Y), Viewport);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller is null)
            return;
        _scroller.ScrollChanged += OnScrollChanged;
        _scrollSubscription = _scroller.GetObservable(ScrollViewer.ViewportProperty).Subscribe(_ => InvalidateVisual());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scroller is not null)
            _scroller.ScrollChanged -= OnScrollChanged;
        _scrollSubscription?.Dispose();
        _scroller = null;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    /// <summary>Scroll <paramref name="rect"/> into view once layout has caught up with whatever moved it.</summary>
    protected void ScrollIntoView(Func<Rect?> rect) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (rect() is { } target)
                this.BringIntoView(target);
        });

    // -- editor -------------------------------------------------------------------

    /// <summary>
    /// Float a spin box over <paramref name="cell"/>, grown to fit its buttons and nudged back
    /// inside <paramref name="bounds"/> rather than clipped by it. Enter or leaving it commits,
    /// Escape abandons.
    /// </summary>
    protected void BeginEdit(Rect cell, Rect bounds, double value, double minimum, double maximum,
        double increment, int decimals, Action<double> commit)
    {
        CloseEditor(commit: false, refocus: false);
        _editorDecimals = decimals;
        _editor.Minimum = (decimal)minimum;
        _editor.Maximum = (decimal)maximum;
        _editor.Increment = (decimal)increment;
        _editor.FormatString = decimals == 0 ? "0" : "0." + new string('0', decimals);
        _editor.Value = Math.Clamp((decimal)Math.Round(value, decimals, MidpointRounding.ToEven), _editor.Minimum, _editor.Maximum);

        _editor.IsVisible = true;
        _editor.Measure(Size.Infinity);
        var width = Math.Max(Math.Max(cell.Width, _editor.DesiredSize.Width), 78);
        var height = Math.Max(cell.Height, _editor.DesiredSize.Height);
        var x = cell.Center.X - width / 2;
        var y = cell.Center.Y - height / 2;
        x = Math.Max(bounds.Left, Math.Min(x, bounds.Right - width));
        y = Math.Max(bounds.Top, Math.Min(y, bounds.Bottom - height));
        _editorRect = new Rect(x, y, width, height);
        _commit = commit;

        InvalidateArrange();
        Dispatcher.UIThread.Post(() =>
        {
            _editor.Focus();
            if (_editor.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } text)
                text.SelectAll();
        });
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CloseEditor(commit: true, refocus: true);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseEditor(commit: false, refocus: true);
                e.Handled = true;
                break;
        }
    }

    private void CloseEditor(bool commit, bool refocus)
    {
        var action = _commit;
        if (action is null)
            return;
        _commit = null;
        var value = EditorValue();
        if (refocus)
            Focus();
        _editor.IsVisible = false;
        if (commit && value is { } number)
            action(number);
    }

    /// <summary>What the spin box holds, reading its text the way Qt's <c>interpretText()</c> does: clamped, rounded to its decimals.</summary>
    private double? EditorValue()
    {
        decimal? value = _editor.Value;
        if (decimal.TryParse(_editor.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var typed))
            value = typed;
        if (value is not { } number)
            return null;
        number = Math.Clamp(number, _editor.Minimum, _editor.Maximum);
        return (double)Math.Round(number, _editorDecimals, MidpointRounding.ToEven);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _editor.Measure(Size.Infinity);
        return MeasureGrid(availableSize);
    }

    protected abstract Size MeasureGrid(Size availableSize);

    protected override Size ArrangeOverride(Size finalSize)
    {
        _editor.Arrange(_editor.IsVisible ? _editorRect : default);
        return finalSize;
    }

    // -- painting helpers -----------------------------------------------------------

    private readonly Dictionary<(string, double, bool, Color), FormattedText> _textCache = [];

    /// <summary>Shared text layouts: the grids draw the same few numbers hundreds of times a frame.</summary>
    protected FormattedText CachedText(string text, double size, Color color, bool bold = false)
    {
        var key = (text, size, bold, color);
        if (!_textCache.TryGetValue(key, out var formatted))
        {
            if (_textCache.Count > 4000)
                _textCache.Clear();
            formatted = Fonts.Text(text, size, color, bold);
            _textCache[key] = formatted;
        }
        return formatted;
    }

    protected static void DrawCentered(DrawingContext context, FormattedText text, Rect rect) =>
        context.DrawText(text, new Point(rect.X + (rect.Width - text.Width) / 2, rect.Y + (rect.Height - text.Height) / 2));
}
