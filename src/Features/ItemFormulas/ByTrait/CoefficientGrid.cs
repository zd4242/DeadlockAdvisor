using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using DeadlockAdvisor.Controls.Art;
using DeadlockAdvisor.Controls.Grids;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Shared.ItemCard;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Features.ItemFormulas.ByTrait;

public class CoefficientEditedEventArgs(RoutedEvent routedEvent, CoefficientRow row, double value) : RoutedEventArgs(routedEvent)
{
    public CoefficientRow Row { get; } = row;
    public double Value { get; } = value;
}

public class SortRequestedEventArgs(RoutedEvent routedEvent, int column) : RoutedEventArgs(routedEvent)
{
    public int Column { get; } = column;
}

/// <summary>
/// Every item against one trait + relation, drawn as one control: Item (with icon), Tier, Shop, the
/// heat-shaded Coefficient, and what the item's stats add. The header sorts; keys go to the view
/// model through the view, and this control handles the mouse, F2 and drawing.
/// </summary>
public class CoefficientGrid : ScrollingGrid
{
    public const double RowHeight = 28;
    public const double HeaderHeight = 33;
    private const double _icon = 20;
    private const double _pad = 5;
    private const double _scrollBarGutter = 12;
    // Tier, Shop, Coefficient, From stats; Item takes the rest.
    private static readonly double[] _fixedWidths = [60, 90, 120, 110];
    private const double _heatLimit = 10;

    public static readonly StyledProperty<IReadOnlyList<CoefficientRow>> RowsProperty =
        AvaloniaProperty.Register<CoefficientGrid, IReadOnlyList<CoefficientRow>>(nameof(Rows), []);

    public static readonly StyledProperty<CoefficientRow?> CurrentRowProperty =
        AvaloniaProperty.Register<CoefficientGrid, CoefficientRow?>(nameof(CurrentRow), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> SortColumnProperty =
        AvaloniaProperty.Register<CoefficientGrid, int>(nameof(SortColumn), -1);

    public static readonly StyledProperty<bool> SortDescendingProperty =
        AvaloniaProperty.Register<CoefficientGrid, bool>(nameof(SortDescending));

    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<CoefficientGrid, int>(nameof(Revision));

    public static readonly RoutedEvent<CoefficientEditedEventArgs> CoefficientEditedEvent =
        RoutedEvent.Register<CoefficientGrid, CoefficientEditedEventArgs>("CoefficientEdited", RoutingStrategies.Bubble);

    public static readonly RoutedEvent<SortRequestedEventArgs> SortRequestedEvent =
        RoutedEvent.Register<CoefficientGrid, SortRequestedEventArgs>("SortRequested", RoutingStrategies.Bubble);

    private int _hoverColumn = -1;
    private object? _tipKey;
    private string? _hoverItem;

    static CoefficientGrid()
    {
        AffectsMeasure<CoefficientGrid>(RowsProperty);
        AffectsRender<CoefficientGrid>(CurrentRowProperty, SortColumnProperty, SortDescendingProperty, RevisionProperty,
            ArtHost.ServiceProperty, ArtHost.RevisionProperty);
    }

    public IReadOnlyList<CoefficientRow> Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public CoefficientRow? CurrentRow
    {
        get => GetValue(CurrentRowProperty);
        set => SetValue(CurrentRowProperty, value);
    }

    public int SortColumn
    {
        get => GetValue(SortColumnProperty);
        set => SetValue(SortColumnProperty, value);
    }

    public bool SortDescending
    {
        get => GetValue(SortDescendingProperty);
        set => SetValue(SortDescendingProperty, value);
    }

    /// <summary>Bump to repaint after row values change.</summary>
    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CurrentRowProperty || change.Property == RowsProperty)
        {
            ScrollIntoView(() => RowRect(CurrentRow) is { } row
                // Grown by the pinned header, so the row lands clear of it rather than under it.
                ? new Rect(row.X, row.Y - HeaderHeight, row.Width, row.Height + HeaderHeight)
                : null);
        }
    }

    // -- geometry -------------------------------------------------------------------

    protected override Size MeasureGrid(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        return new Size(width, HeaderHeight + Rows.Count * RowHeight);
    }

    /// <summary>Column edges left to right. A scroll bar overlays the right edge, so the columns stop short of it when it shows.</summary>
    private double[] ColumnEdges()
    {
        var right = Bounds.Width - (Bounds.Height > Viewport.Height + 0.5 ? _scrollBarGutter : 0);
        var edges = new double[6];
        edges[5] = right;
        for (var column = 4; column >= 1; column--)
            edges[column] = edges[column + 1] - _fixedWidths[column - 1];
        edges[0] = 0;
        return edges;
    }

    private int IndexOf(CoefficientRow? row)
    {
        if (row is null)
            return -1;
        for (var index = 0; index < Rows.Count; index++)
        {
            if (ReferenceEquals(Rows[index], row))
                return index;
        }
        return -1;
    }

    private Rect? RowRect(CoefficientRow? row)
    {
        var index = IndexOf(row);
        return index < 0 ? null : new Rect(0, HeaderHeight + index * RowHeight, Bounds.Width, RowHeight);
    }

    private (int Row, int Column, bool Header) HitTest(Point point)
    {
        var edges = ColumnEdges();
        var column = -1;
        for (var index = 0; index < 5; index++)
        {
            if (point.X >= edges[index] && point.X < edges[index + 1])
                column = index;
        }
        if (point.Y - Offset.Y < HeaderHeight)
            return (-1, column, true);
        var row = (int)Math.Floor((point.Y - HeaderHeight) / RowHeight);
        return (row >= 0 && row < Rows.Count ? row : -1, column, false);
    }

    // -- input ----------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        UpdateCard(null);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var (row, column, header) = HitTest(e.GetPosition(this));
        if (header)
        {
            if (column >= 0)
                RaiseEvent(new SortRequestedEventArgs(SortRequestedEvent, column));
            e.Handled = true;
            return;
        }
        if (row < 0)
            return;
        SetCurrentValue(CurrentRowProperty, Rows[row]);
        Focus();
        // Only the coefficient is editable.
        if (e.ClickCount == 2 && column == ByTraitViewModel.CoefficientColumn)
            BeginEditCurrent();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F2 && e.KeyModifiers == KeyModifiers.None && !IsEditing)
        {
            BeginEditCurrent();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var (row, column, header) = HitTest(point);
        var hoverColumn = header ? column : -1;
        if (hoverColumn != _hoverColumn)
        {
            _hoverColumn = hoverColumn;
            InvalidateVisual();
        }
        UpdateToolTip(row, column, header);

        // The item card follows the icon only, not the whole name cell, so reading down the names doesn't pop cards up.
        var onIcon = !header && row >= 0 && column == 0 && point.X < _pad + _icon + 8;
        UpdateCard(onIcon ? Rows[row].ItemId : null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverColumn = -1;
        UpdateToolTip(-1, -1, false);
        UpdateCard(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        UpdateCard(null);
    }

    private void UpdateCard(string? itemId)
    {
        if (itemId == _hoverItem)
            return;
        _hoverItem = itemId;
        var presenter = ItemCardHover.GetPresenter(this);
        if (itemId is null)
            presenter?.Hide(this);
        else
            presenter?.Hover(this, itemId);
    }

    private void UpdateToolTip(int row, int column, bool header)
    {
        string? tip = null;
        if (header && column == 4)
            tip = ByTraitViewModel.FromStatsTip;
        else if (!header && row >= 0 && column == 4)
            tip = Rows[row].FromStatsTip;

        var key = (header, row, column);
        if (Equals(key, _tipKey))
            return;
        _tipKey = key;
        ToolTip.SetTip(this, tip);
    }

    /// <summary>A spin box for decimals: one place, in steps of 0.5, ±20.</summary>
    private void BeginEditCurrent()
    {
        if (CurrentRow is not { } row || RowRect(row) is not { } rect)
            return;
        var edges = ColumnEdges();
        var cell = new Rect(edges[3], rect.Y, edges[4] - edges[3], RowHeight);
        var view = ViewRect;
        var bounds = new Rect(view.X, view.Y + HeaderHeight, view.Width, Math.Max(0, view.Height - HeaderHeight));
        BeginEdit(cell, bounds, row.Coefficient, -20, 20, 0.5, 1,
            value => RaiseEvent(new CoefficientEditedEventArgs(CoefficientEditedEvent, row, value)));
    }

    // -- painting -------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        var view = ViewRect;
        var edges = ColumnEdges();
        context.FillRectangle(new SolidColorBrush(Palette.Surface), view);

        var first = Math.Max(0, (int)Math.Floor((view.Top) / RowHeight) - 1);
        var last = Math.Min(Rows.Count - 1, (int)Math.Floor((view.Bottom - HeaderHeight) / RowHeight));
        using (context.PushClip(new Rect(view.X, view.Y + HeaderHeight, view.Width, Math.Max(0, view.Height - HeaderHeight))))
        {
            for (var index = first; index <= last; index++)
                PaintRow(context, Rows[index], index, edges);
        }
        PaintHeader(context, view, edges);
    }

    private void PaintRow(DrawingContext context, CoefficientRow row, int index, double[] edges)
    {
        var y = HeaderHeight + index * RowHeight;
        var width = edges[5];
        var selected = ReferenceEquals(row, CurrentRow);
        // Stripes follow the sorted order, hidden rows included, as the Python grid's did.
        var alternate = row.OrderIndex % 2 == 1;
        var background = selected ? Palette.Surface4 : HeatCell.Background(alternate);
        context.FillRectangle(new SolidColorBrush(background), new Rect(0, y, width, RowHeight));

        var text = selected ? Palette.Text : (Color?)null;

        // Item: icon then name.
        var iconRect = new Rect(_pad, y + (RowHeight - _icon) / 2, _icon, _icon);
        ArtPainter.Draw(context, this, ArtKind.Item, row.ItemId, row.Name, iconRect, tint: row.ShopColor);
        var name = Fonts.Text(row.Name, 13, Palette.Text);
        name.MaxTextWidth = Math.Max(1, edges[1] - iconRect.Right - 6 - _pad);
        name.MaxLineCount = 1;
        name.Trimming = TextTrimming.CharacterEllipsis;
        context.DrawText(name, new Point(iconRect.Right + 6, y + (RowHeight - name.Height) / 2));

        DrawCentered(context, CachedText(row.TierText, 13, text ?? row.TierColor), new Rect(edges[1], y, edges[2] - edges[1], RowHeight));
        var shop = CachedText(row.Shop, 13, text ?? row.ShopColor);
        context.DrawText(shop, new Point(edges[2] + _pad, y + (RowHeight - shop.Height) / 2));

        var cell = new Rect(edges[3], y, edges[4] - edges[3], RowHeight);
        HeatCell.Paint(context, cell, row.Coefficient, _heatLimit, alternate, selected);
        DrawCentered(context, row.Coefficient == 0
            ? CachedText("·", 12, Palette.TextFaint)
            : CachedText(Format.Num(row.Coefficient), 13, Palette.Text, bold: true), cell);

        if (row.FromStatsText.Length > 0)
            DrawCentered(context, CachedText(row.FromStatsText, 13, text ?? Palette.TextDim), new Rect(edges[4], y, edges[5] - edges[4], RowHeight));
    }

    private void PaintHeader(DrawingContext context, Rect view, double[] edges)
    {
        var border = new Pen(new SolidColorBrush(Palette.Border), 1);
        var top = view.Y;
        context.FillRectangle(new SolidColorBrush(Palette.Surface2), new Rect(view.X, top, view.Width, HeaderHeight));
        for (var column = 0; column < 5; column++)
        {
            var rect = new Rect(edges[column], top, edges[column + 1] - edges[column], HeaderHeight);
            var hovered = column == _hoverColumn;
            if (hovered)
                context.FillRectangle(new SolidColorBrush(Palette.Surface3), rect);
            context.DrawLine(border, new Point(rect.Right - 0.5, rect.Top), new Point(rect.Right - 0.5, rect.Bottom));

            var label = CachedText(ByTraitViewModel.ColumnNames[column], 11, hovered ? Palette.Text : Palette.TextDim, bold: true);
            var labelY = top + (HeaderHeight - label.Height) / 2;
            if (column == 0)
                context.DrawText(label, new Point(rect.X + 8, labelY));
            else
                DrawCentered(context, label, rect);

            if (column == SortColumn)
                PaintSortArrow(context, rect, SortDescending);
        }
        context.DrawLine(border, new Point(view.X, top + HeaderHeight - 0.5), new Point(view.Right, top + HeaderHeight - 0.5));
    }

    private static void PaintSortArrow(DrawingContext context, Rect header, bool descending)
    {
        const double size = 7;
        var x = header.Right - 8 - size;
        var middle = header.Center.Y;
        var tipY = descending ? middle + size / 4 : middle - size / 4;
        var baseY = descending ? middle - size / 4 : middle + size / 4;
        var pen = new Pen(new SolidColorBrush(Palette.TextDim), 1.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        context.DrawGeometry(null, pen, new PolylineGeometry(
            [new Point(x, baseY), new Point(x + size / 2, tipY), new Point(x + size, baseY)], isFilled: false));
    }
}
