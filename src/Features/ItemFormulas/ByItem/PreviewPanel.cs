using Avalonia.Layout;
using Avalonia.Media;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Features.ItemFormulas.ByItem;

/// <summary>
/// "Would apply to": per relation, a coloured heading and a small table of the heroes the item
/// responds to, with each trait's piece of the sum in its own column. Built in code: the column
/// count changes with the item.
/// </summary>
public class PreviewPanel : StackPanel
{
    public const string EmptyHint =
        "Nothing yet. Once this item has rules and the relevant heroes have those traits rated, "
        + "the heroes it responds to show up here.";

    public static readonly StyledProperty<IReadOnlyList<PreviewGroup>?> GroupsProperty =
        AvaloniaProperty.Register<PreviewPanel, IReadOnlyList<PreviewGroup>?>(nameof(Groups));

    public PreviewPanel()
    {
        Spacing = 4;
    }

    protected override Type StyleKeyOverride => typeof(StackPanel);

    /// <summary>Null with no item picked; empty, which shows a hint, when the item fires for nobody.</summary>
    public IReadOnlyList<PreviewGroup>? Groups
    {
        get => GetValue(GroupsProperty);
        set => SetValue(GroupsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GroupsProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        if (Groups is null)
            return;
        foreach (var group in Groups)
        {
            Children.Add(new TextBlock
            {
                Text = group.Title,
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(group.Color),
            });
            Children.Add(BuildTable(group));
        }
        if (Groups.Count == 0)
            Children.Add(new TextBlock { Text = EmptyHint, TextWrapping = TextWrapping.Wrap, Classes = { "hint" } });
    }

    private static Grid BuildTable(PreviewGroup group)
    {
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var column = 0; column <= group.TraitColumns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        for (var index = 0; index < group.Rows.Count; index++)
        {
            var row = group.Rows[index];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Place(grid, new TextBlock { Text = row.HeroName, Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center }, index, 0);

            for (var column = 0; column < row.Pieces.Count; column++)
            {
                if (row.Pieces[column] is not { } piece)
                    continue;
                var label = new PieceLabel(piece.Amount, piece.Color)
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTip.SetTip(label, piece.Tip);
                Place(grid, label, index, column + 1);
            }

            var value = new TextBlock
            {
                Text = row.Value,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(row.IsBest ? Palette.Accent : Palette.TextDim),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(value, row.Tip);
            Place(grid, value, index, group.TraitColumns + 1);
        }
        return grid;
    }

    private static void Place(Grid grid, Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }
}
