using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace DeadlockAdvisor.Features.Shared.ItemCard;

/// <summary>
/// The one item card popup a window shares. It never takes focus or clicks, so hovering an icon
/// can't steal keystrokes from the grid you're typing in. Placed below-right of the cursor and
/// kept on screen.
/// </summary>
public class ItemCardPresenter : Control
{
    /// <summary>Long enough that sweeping across a list doesn't flash cards.</summary>
    public static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(250);
    private const double _cursorGap = 18;

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<ItemCardPresenter, double>(nameof(Zoom), 1.0);

    private readonly Popup _popup;
    private readonly LayoutTransformControl _scaler;
    private readonly DispatcherTimer _timer;
    private Control? _anchor;
    private string? _itemId;

    public ItemCardPresenter()
    {
        IsHitTestVisible = false;
        _scaler = new LayoutTransformControl { IsHitTestVisible = false };
        _popup = new Popup
        {
            Child = _scaler,
            Placement = PlacementMode.Pointer,
            HorizontalOffset = _cursorGap,
            VerticalOffset = _cursorGap,
            IsLightDismissEnabled = false,
            OverlayInputPassThroughElement = null,
            IsHitTestVisible = false,
        };
        // It scales its own card by Zoom, which is the same zoom the other popups take from their target.
        _popup.InheritsTransform = false;
        LogicalChildren.Add(_popup);
        VisualChildren.Add(_popup);

        _timer = new DispatcherTimer { Interval = ShowDelay };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Show();
        };
    }

    /// <summary>Builds the card for an item id, or null for an unknown item.</summary>
    public Func<string, Control?>? CardFactory { get; set; }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>The item whose card is up, or null.</summary>
    public string? ShownItemId => _popup.IsOpen ? _itemId : null;

    public void Hover(Control anchor, string? itemId)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            Hide();
            return;
        }
        if (ReferenceEquals(anchor, _anchor) && itemId == _itemId)
            return;

        _anchor = anchor;
        _itemId = itemId;
        // Once a card is up, moving to the next icon swaps it straight away, the way tooltips do.
        if (_popup.IsOpen)
        {
            Show();
            return;
        }
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Hide the card; only if <paramref name="anchor"/> is what it's showing for, when given.</summary>
    public void Hide(Control? anchor = null)
    {
        if (anchor is not null && !ReferenceEquals(anchor, _anchor))
            return;
        _timer.Stop();
        _anchor = null;
        _itemId = null;
        _popup.IsOpen = false;
    }

    private void Show()
    {
        var card = _itemId is null ? null : CardFactory?.Invoke(_itemId);
        if (card is null || _anchor is null)
        {
            Hide();
            return;
        }

        card.Width = ItemCardBuilder.Width;
        // The popup is its own visual root, so the art service isn't inherited into it.
        Controls.Art.ArtHost.SetService(_scaler, Controls.Art.ArtHost.GetService(this));
        _scaler.LayoutTransform = new ScaleTransform(Zoom, Zoom);
        _scaler.Child = card;
        _popup.PlacementTarget = _anchor;
        // Re-opening places the card at the cursor again.
        _popup.IsOpen = false;
        _popup.IsOpen = true;
    }

    protected override Size MeasureOverride(Size availableSize) => default;
}
