using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Controls.Art;
using DeadlockAdvisor.Controls.Grids;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Features.HeroTraits;

public class CellEditedEventArgs(RoutedEvent routedEvent, int row, int column, double value) : RoutedEventArgs(routedEvent)
{
    public int Row { get; } = row;
    public int Column { get; } = column;
    public double Value { get; } = value;
}

/// <summary>
/// The hero × trait grid as one drawn control: heat-shaded cells, hero names with portraits down
/// the side, and trait names on a 45° slant across the top.
/// <para>
/// ~23 columns only fit if the labels don't lie flat, but stood on end they took a head tilt to
/// read. Each label sits in a parallelogram leaning off the top of its own column, with alternating
/// shading, so it's obvious which column a label belongs to even though the text drifts right. A
/// click high on a label lands above a column further right, so hit testing slides the point back
/// down the slope first.
/// </para>
/// The header sorts; keys go to the view model through the view, and this control handles the
/// mouse, F2 and drawing.
/// </summary>
public class TraitGrid : ScrollingGrid
{
    public const double ColumnWidth = 44;
    public const double RowHeight = 30;
    public const double RowHeaderWidth = 160;
    private const double _headerFontSize = 12;
    private const double _maxHeaderHeight = 150;
    // Distance along the slant from the column's foot to the text: half a line at least, so the
    // text's lower corner clears the bottom edge.
    private const double _textPad = 10;
    private const double _portrait = 18;
    // Along the slant, between the end of the sorted trait's label and its arrow.
    private const double _arrowGap = 5;
    private static readonly double _sin45 = Math.Sqrt(0.5);
    private static readonly Color _darkText = Color.Parse("#1a1a20");

    public static readonly StyledProperty<IReadOnlyList<Hero>> HeroesProperty =
        AvaloniaProperty.Register<TraitGrid, IReadOnlyList<Hero>>(nameof(Heroes), []);

    public static readonly StyledProperty<IReadOnlyList<Category>> CategoriesProperty =
        AvaloniaProperty.Register<TraitGrid, IReadOnlyList<Category>>(nameof(Categories), []);

    public static readonly StyledProperty<IReadOnlyList<int>> RowOrderProperty =
        AvaloniaProperty.Register<TraitGrid, IReadOnlyList<int>>(nameof(RowOrder), []);

    public static readonly StyledProperty<IReadOnlyList<int>> VisibleRowsProperty =
        AvaloniaProperty.Register<TraitGrid, IReadOnlyList<int>>(nameof(VisibleRows), []);

    public static readonly StyledProperty<Func<string, string, double>?> ValueOfProperty =
        AvaloniaProperty.Register<TraitGrid, Func<string, string, double>?>(nameof(ValueOf));

    public static readonly StyledProperty<int> CurrentRowProperty =
        AvaloniaProperty.Register<TraitGrid, int>(nameof(CurrentRow), -1, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> CurrentColumnProperty =
        AvaloniaProperty.Register<TraitGrid, int>(nameof(CurrentColumn), -1, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> PendingTextProperty =
        AvaloniaProperty.Register<TraitGrid, string?>(nameof(PendingText));

    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<TraitGrid, int>(nameof(Revision));

    public static readonly RoutedEvent<CellEditedEventArgs> CellEditedEvent =
        RoutedEvent.Register<TraitGrid, CellEditedEventArgs>("CellEdited", RoutingStrategies.Bubble);

    private Dictionary<int, int> _displayIndex = [];
    private Dictionary<int, int> _orderIndex = [];
    private double _headerHeight = 60;
    private int _hoverColumn = -1;
    private int _hoverRow = -1;
    private object? _tipKey;

    static TraitGrid()
    {
        AffectsMeasure<TraitGrid>(HeroesProperty, CategoriesProperty, VisibleRowsProperty);
        AffectsRender<TraitGrid>(RowOrderProperty, ValueOfProperty, CurrentRowProperty, CurrentColumnProperty, PendingTextProperty,
            RevisionProperty, ArtHost.ServiceProperty, ArtHost.RevisionProperty);
    }

    public IReadOnlyList<Hero> Heroes
    {
        get => GetValue(HeroesProperty);
        set => SetValue(HeroesProperty, value);
    }

    public IReadOnlyList<Category> Categories
    {
        get => GetValue(CategoriesProperty);
        set => SetValue(CategoriesProperty, value);
    }

    /// <summary>Every index into <see cref="Heroes"/> in the sorted order, hidden ones included; the stripes follow it.</summary>
    public IReadOnlyList<int> RowOrder
    {
        get => GetValue(RowOrderProperty);
        set => SetValue(RowOrderProperty, value);
    }

    /// <summary>Indices into <see cref="Heroes"/> to show, in order.</summary>
    public IReadOnlyList<int> VisibleRows
    {
        get => GetValue(VisibleRowsProperty);
        set => SetValue(VisibleRowsProperty, value);
    }

    /// <summary>(hero id, category id) → stored score.</summary>
    public Func<string, string, double>? ValueOf
    {
        get => GetValue(ValueOfProperty);
        set => SetValue(ValueOfProperty, value);
    }

    public int CurrentRow
    {
        get => GetValue(CurrentRowProperty);
        set => SetValue(CurrentRowProperty, value);
    }

    public int CurrentColumn
    {
        get => GetValue(CurrentColumnProperty);
        set => SetValue(CurrentColumnProperty, value);
    }

    public string? PendingText
    {
        get => GetValue(PendingTextProperty);
        set => SetValue(PendingTextProperty, value);
    }

    /// <summary>Bump to repaint after the values behind <see cref="ValueOf"/> change.</summary>
    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    /// <summary>How tall the slanted trait header is, sized to the longest label.</summary>
    public double HeaderHeight => _headerHeight;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowOrderProperty)
            _orderIndex = IndexOf(RowOrder);
        else if (change.Property == VisibleRowsProperty)
            _displayIndex = IndexOf(VisibleRows);
        else if (change.Property == CategoriesProperty)
            _headerHeight = FitHeaderHeight();

        // A sort or filter moves the current cell as surely as the keys do.
        if (change.Property == CurrentRowProperty || change.Property == CurrentColumnProperty || change.Property == VisibleRowsProperty)
        {
            ScrollIntoView(() => CellRect(CurrentRow, CurrentColumn) is { } cell
                // Grown by the pinned headers, so the cell lands clear of them rather than under them.
                ? new Rect(cell.X - RowHeaderWidth, cell.Y - _headerHeight, cell.Width + RowHeaderWidth, cell.Height + _headerHeight)
                : null);
        }
    }

    /// <summary>Hero index → its position in <paramref name="rows"/>.</summary>
    private static Dictionary<int, int> IndexOf(IReadOnlyList<int> rows) =>
        rows.Select((row, index) => (row, index)).ToDictionary(pair => pair.row, pair => pair.index);

    // -- geometry -------------------------------------------------------------------

    /// <summary>
    /// Along the slant: padding, the longest label and room for a sort arrow after it, half a line
    /// for the far top corner, a little air. At 45° that run rises by run × sin 45°.
    /// </summary>
    private double FitHeaderHeight()
    {
        var longest = Categories.Select(category => Fonts.Text(Label(category), _headerFontSize, Palette.Text, bold: true).Width)
            .DefaultIfEmpty(80).Max();
        var line = Fonts.Text("Ag", _headerFontSize, Palette.Text, bold: true).Height;
        var run = _textPad + longest + _arrowGap + SortArrowSize + line / 2 + 6;
        return Math.Min(_maxHeaderHeight, Math.Ceiling(run * _sin45));
    }

    private static string Label(Category category) => category.ShortName + (category.IsSigned ? " ±" : "");

    protected override Size MeasureGrid(Size availableSize) =>
        // The last labels lean past the last column, so the width leaves room for their overhang.
        new(RowHeaderWidth + Categories.Count * ColumnWidth + _headerHeight, _headerHeight + VisibleRows.Count * RowHeight);

    private static double ColumnX(int column) => RowHeaderWidth + column * ColumnWidth;

    private double RowY(int displayIndex) => _headerHeight + displayIndex * RowHeight;

    private Rect? CellRect(int row, int column)
    {
        if (column < 0 || column >= Categories.Count || !_displayIndex.TryGetValue(row, out var display))
            return null;
        return new Rect(ColumnX(column), RowY(display), ColumnWidth, RowHeight);
    }

    private enum Region
    {
        None,
        Cell,
        ColumnHeader,
        RowHeader,
    }

    private (Region Region, int Row, int Column) HitTest(Point point)
    {
        var offset = Offset;
        var inHeader = point.Y - offset.Y < _headerHeight;
        var inRowHeader = point.X - offset.X < RowHeaderWidth;
        if (inHeader && inRowHeader)
            return (Region.None, -1, -1);

        if (inHeader)
        {
            // Slide the point back down the slope to the header's foot, then ask which column that is.
            var rise = offset.Y + _headerHeight - point.Y;
            var column = (int)Math.Floor((point.X - rise - RowHeaderWidth) / ColumnWidth);
            return column >= 0 && column < Categories.Count ? (Region.ColumnHeader, -1, column) : (Region.None, -1, -1);
        }

        var display = (int)Math.Floor((point.Y - _headerHeight) / RowHeight);
        if (display < 0 || display >= VisibleRows.Count)
            return (Region.None, -1, -1);
        var row = VisibleRows[display];
        if (inRowHeader)
            return (Region.RowHeader, row, -1);

        var cellColumn = (int)Math.Floor((point.X - RowHeaderWidth) / ColumnWidth);
        return cellColumn >= 0 && cellColumn < Categories.Count ? (Region.Cell, row, cellColumn) : (Region.None, -1, -1);
    }

    // -- input ----------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var (region, row, column) = HitTest(e.GetPosition(this));
        switch (region)
        {
            case Region.Cell:
                SetCurrentValue(CurrentRowProperty, row);
                SetCurrentValue(CurrentColumnProperty, column);
                Focus();
                if (e.ClickCount == 2)
                    BeginEditCurrent();
                e.Handled = true;
                break;
            case Region.ColumnHeader:
                Focus();
                RaiseEvent(new SortRequestedEventArgs(SortRequestedEvent, column));
                e.Handled = true;
                break;
            // Jump to that hero's first trait.
            case Region.RowHeader when Categories.Count > 0:
                SetCurrentValue(CurrentRowProperty, row);
                SetCurrentValue(CurrentColumnProperty, 0);
                Focus();
                e.Handled = true;
                break;
        }
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
        var (region, row, column) = HitTest(e.GetPosition(this));
        var hoverColumn = region == Region.ColumnHeader ? column : -1;
        var hoverRow = region == Region.RowHeader ? row : -1;
        if (hoverColumn != _hoverColumn || hoverRow != _hoverRow)
        {
            (_hoverColumn, _hoverRow) = (hoverColumn, hoverRow);
            InvalidateVisual();
        }
        UpdateToolTip(region, row, column);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        (_hoverColumn, _hoverRow) = (-1, -1);
        UpdateToolTip(Region.None, -1, -1);
        InvalidateVisual();
    }

    /// <summary>A spin box for mouse-driven edits: whole numbers on the trait's scale, in steps of 5.</summary>
    private void BeginEditCurrent()
    {
        var (row, column) = (CurrentRow, CurrentColumn);
        if (CellRect(row, column) is not { } cell || ValueOf is null)
            return;
        var category = Categories[column];
        var offset = Offset;
        var viewport = Viewport;
        var bounds = new Rect(offset.X + RowHeaderWidth, offset.Y + _headerHeight,
            Math.Max(0, viewport.Width - RowHeaderWidth), Math.Max(0, viewport.Height - _headerHeight));
        var value = ValueOf(Heroes[row].HeroId, category.CategoryId);
        BeginEdit(cell, bounds, value, Math.Truncate(category.ScaleMin), Math.Truncate(category.ScaleMax), 5, 0,
            edited => RaiseEvent(new CellEditedEventArgs(CellEditedEvent, row, column, edited)));
    }

    private void UpdateToolTip(Region region, int row, int column)
    {
        var key = (region, row, column);
        if (Equals(key, _tipKey))
            return;
        _tipKey = key;
        ToolTip.SetTip(this, region switch
        {
            Region.Cell => RichText.Create(CellTip(Heroes[row], Categories[column])),
            Region.ColumnHeader => RichText.Create(HeaderTip(Categories[column])),
            Region.RowHeader => HeroTip(Heroes[row]),
            _ => null,
        });
    }

    private static List<TextSpan> CellTip(Hero hero, Category category) =>
    [
        new(hero.HeroName, Bold: true),
        new($" — {category.CategoryName}"),
        TextSpan.LineBreak,
        new(category.Description),
        TextSpan.LineBreak,
        new(ScaleText(category), Italic: true),
    ];

    private static List<TextSpan> HeaderTip(Category category) =>
    [
        new(category.CategoryName, Bold: true),
        TextSpan.LineBreak,
        new(category.Description),
        TextSpan.LineBreak,
        new(ScaleText(category), Italic: true),
    ];

    private static string ScaleText(Category category) => $"scale {Format.Num(category.ScaleMin)} to {Format.Num(category.ScaleMax)}";

    private string HeroTip(Hero hero)
    {
        var filled = ValueOf is null ? 0 : Categories.Count(category => ValueOf(hero.HeroId, category.CategoryId) != 0);
        return $"{hero.HeroName} — {filled}/{Categories.Count} traits rated";
    }

    // -- painting -------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        var view = ViewRect;
        context.FillRectangle(new SolidColorBrush(Palette.Surface), view);

        var cells = new Rect(view.X + RowHeaderWidth, view.Y + _headerHeight,
            Math.Max(0, view.Width - RowHeaderWidth), Math.Max(0, view.Height - _headerHeight));
        var firstDisplay = Math.Max(0, (int)Math.Floor((cells.Top - _headerHeight) / RowHeight));
        var lastDisplay = Math.Min(VisibleRows.Count - 1, (int)Math.Floor((cells.Bottom - _headerHeight) / RowHeight));
        var firstColumn = Math.Max(0, (int)Math.Floor((cells.Left - RowHeaderWidth) / ColumnWidth));
        var lastColumn = Math.Min(Categories.Count - 1, (int)Math.Floor((cells.Right - RowHeaderWidth) / ColumnWidth));

        using (context.PushClip(cells))
        {
            for (var display = firstDisplay; display <= lastDisplay; display++)
            {
                for (var column = firstColumn; column <= lastColumn; column++)
                    PaintCell(context, VisibleRows[display], display, column);
            }
        }

        using (context.PushClip(new Rect(view.X, cells.Y, RowHeaderWidth, cells.Height)))
        {
            for (var display = firstDisplay; display <= lastDisplay; display++)
                PaintRowHeader(context, VisibleRows[display], new Rect(view.X, RowY(display), RowHeaderWidth, RowHeight));
        }

        using (context.PushClip(new Rect(cells.X, view.Y, cells.Width, _headerHeight)))
            PaintColumnHeader(context, view);

        var border = new Pen(new SolidColorBrush(Palette.Border), 1);
        context.DrawLine(border, new Point(view.X, view.Y + _headerHeight - 0.5), new Point(view.Right, view.Y + _headerHeight - 0.5));
    }

    private void PaintCell(DrawingContext context, int row, int display, int column)
    {
        var hero = Heroes[row];
        var category = Categories[column];
        var rect = new Rect(ColumnX(column), RowY(display), ColumnWidth, RowHeight);
        var value = ValueOf?.Invoke(hero.HeroId, category.CategoryId) ?? 0;
        var limit = Math.Max(Math.Abs(category.ScaleMin), Math.Abs(category.ScaleMax));
        var current = row == CurrentRow && column == CurrentColumn;

        // Alternate by the hero's place in the full sorted list, not on screen, as the Python grid does.
        HeatCell.Paint(context, rect, value, limit, alternate: _orderIndex.GetValueOrDefault(row) % 2 == 1, selected: current);

        var border = new Pen(new SolidColorBrush(Palette.Border), 1);
        context.DrawLine(border, new Point(rect.Right - 0.5, rect.Top), new Point(rect.Right - 0.5, rect.Bottom));
        context.DrawLine(border, new Point(rect.Left, rect.Bottom - 0.5), new Point(rect.Right, rect.Bottom - 0.5));

        if (current && PendingText is { } pending)
            DrawCentered(context, CachedText(pending, 13, Palette.Accent, bold: true), rect);
        else if (value == 0)
            DrawCentered(context, CachedText("·", 12, Palette.TextFaint), rect);
        else
            DrawCentered(context, CachedText(Format.Num(value), Math.Abs(value) >= 100 ? 12 : 13,
                Math.Abs(value) / (limit == 0 ? 1 : limit) < 0.75 ? Palette.Text : _darkText, bold: true), rect);
    }

    /// <summary>The hero names down the side, lit up to match the trait header so the current cell reads off both edges.</summary>
    private void PaintRowHeader(DrawingContext context, int row, Rect rect)
    {
        var hero = Heroes[row];
        var selected = row == CurrentRow;
        var hovered = row == _hoverRow;
        context.FillRectangle(new SolidColorBrush(selected || hovered ? Palette.Surface3 : Palette.Surface2), rect);
        var border = new Pen(new SolidColorBrush(Palette.Border), 1);
        context.DrawLine(border, new Point(rect.Right - 0.5, rect.Top), new Point(rect.Right - 0.5, rect.Bottom));
        context.DrawLine(border, new Point(rect.Left, rect.Bottom - 0.5), new Point(rect.Right, rect.Bottom - 0.5));

        var left = rect.X + 8;
        var portrait = new Rect(left, rect.Y + Math.Floor((rect.Height - _portrait) / 2), _portrait, _portrait);
        ArtPainter.Draw(context, this, ArtKind.Hero, hero.HeroId, hero.HeroName, portrait, radius: 4);
        left += _portrait + 8;

        var name = Fonts.Text(hero.HeroName, 11, selected ? Palette.Accent : hovered ? Palette.Text : Palette.TextDim, bold: selected);
        name.MaxTextWidth = Math.Max(1, rect.Right - left - 6);
        name.MaxLineCount = 1;
        name.Trimming = TextTrimming.CharacterEllipsis;
        context.DrawText(name, new Point(left, rect.Y + (rect.Height - name.Height) / 2));
    }

    private void PaintColumnHeader(DrawingContext context, Rect view)
    {
        var height = _headerHeight;
        var top = view.Y;
        var bottom = top + height;
        var line = Fonts.Text("Ag", _headerFontSize, Palette.Text, bold: true).Height;
        // Longest text that still ends inside the strip.
        var room = height / _sin45 - _textPad - line / 2;
        var edge = new Pen(new SolidColorBrush(Palette.Border), 1);

        for (var column = 0; column < Categories.Count; column++)
        {
            var x = ColumnX(column);
            const double w = ColumnWidth;
            // Skip it unless the column or its overhang is in view.
            if (x + w + height < view.Left || x > view.Right)
                continue;

            var isCurrent = column == CurrentColumn;
            var isHover = column == _hoverColumn;
            var strip = new PolylineGeometry(
            [
                new Point(x, bottom), new Point(x + w, bottom),
                new Point(x + w + height, top), new Point(x + height, top),
            ], isFilled: true);
            var fill = isCurrent ? Palette.Mix(Palette.Surface3, Palette.Accent, 0.14)
                : isHover ? Palette.Surface3
                : column % 2 == 0 ? Palette.Surface2 : Palette.Mix(Palette.Surface2, Palette.Surface3, 0.5);
            context.DrawGeometry(new SolidColorBrush(fill), null, strip);

            // The right-hand slanted edge carries on the grid's own column rule, so the eye can
            // follow a label straight down.
            var right = x + w - 0.5;
            context.DrawLine(edge, new Point(right, bottom), new Point(right + height, top));

            var sorted = column == SortColumn;
            var text = Fonts.Text(Label(Categories[column]), _headerFontSize,
                isCurrent ? Palette.Accent : isHover ? Palette.Text : Palette.TextDim, bold: isCurrent);
            text.MaxTextWidth = Math.Max(1, room - (sorted ? _arrowGap + SortArrowSize : 0));
            text.MaxLineCount = 1;
            text.Trimming = TextTrimming.CharacterEllipsis;
            var slant = Matrix.CreateRotation(-Math.PI / 4) * Matrix.CreateTranslation(x + w / 2, bottom);
            using (context.PushTransform(slant))
                context.DrawText(text, new Point(_textPad, -text.Height / 2));

            // Follows the label up the slant but stays upright, so up and down still read as up and down.
            if (sorted)
                DrawSortArrow(context, new Point(_textPad + text.Width + _arrowGap + SortArrowSize / 2, 0).Transform(slant), SortDescending);
        }
    }
}
