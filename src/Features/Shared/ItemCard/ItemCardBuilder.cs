using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using DeadlockAdvisor.Controls.Art;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Features.Shared.ItemCard;

/// <summary>
/// The in-game item card: cost and tier up top, the item's innate stats, then each passive and
/// active with its description and numbers, laid out the way the shop shows them. The numbers come
/// from item_tooltips.json (written by Data → Sync from Game API); without it the card still shows
/// its header and says where the rest comes from.
/// </summary>
public static class ItemCardBuilder
{
    public const double Width = 330;

    public static Control Build(DataStore store, Item item)
    {
        var shop = Palette.ShopColor(item.Category);
        var layout = new StackPanel();
        layout.Children.Add(Header(item, shop));

        if (!store.ItemTooltips.TryGetValue(item.ItemId, out var tooltip))
        {
            var body = Padded(layout);
            body.Children.Add(Label("Run Data › Sync from Game API to load this item's stats.", 12, Palette.TextFaint, wrap: true));
            return Frame(layout);
        }

        foreach (var section in tooltip.Sections)
        {
            if (section.Kind != "innate")
                layout.Children.Add(SectionStrip(section));
            var body = Padded(layout);
            foreach (var block in section.Blocks)
                AddBlock(body, block, innate: section.Kind == "innate");
        }

        var builtFrom = tooltip.Components.Where(store.Items.ContainsKey).ToList();
        var upgrades = store.UpgradesTo(item.ItemId);
        if (builtFrom.Count > 0 || upgrades.Count > 0)
        {
            layout.Children.Add(new Border { Height = 1, Background = Brush(Palette.Border) });
            var body = Padded(layout);
            if (builtFrom.Count > 0)
                body.Children.Add(ItemLinks(store, "Built from", builtFrom));
            if (upgrades.Count > 0)
                body.Children.Add(ItemLinks(store, "Upgrades to", upgrades));
        }
        return Frame(layout);
    }

    private static Border Frame(Control content) => new()
    {
        Background = Brush(Palette.Surface),
        BorderBrush = Brush(Palette.BorderStrong),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        ClipToBounds = true,
        Child = content,
    };

    private static IBrush Brush(Color color) => new SolidColorBrush(color);

    private static TextBlock Label(string text, double size, Color color, bool bold = false, bool italic = false, bool wrap = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Brush(color),
        FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
    };

    private static StackPanel Padded(Panel parent)
    {
        var body = new StackPanel { Margin = new Thickness(12, 10), Spacing = 8 };
        parent.Children.Add(body);
        return body;
    }

    private static Border Header(Item item, Color shop)
    {
        var icon = new ArtImage
        {
            Kind = ArtKind.Item,
            ArtId = item.ItemId,
            ArtName = item.ItemName,
            Tint = shop,
            Width = 46,
            Height = 46,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var names = new StackPanel { Spacing = 2, Margin = new Thickness(10, 0, 0, 0) };
        names.Children.Add(Label(item.ItemName, 17, Palette.Text, bold: true, wrap: true));
        names.Children.Add(Label($"{Format.Title(item.Category)}  ·  Tier {item.Tier}", 12, shop, bold: true));

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row.Children.Add(icon);
        Grid.SetColumn(names, 1);
        row.Children.Add(names);
        if (item.Cost != 0)
        {
            var cost = Label(Format.Thousands(item.Cost), 16, Palette.Accent, bold: true);
            cost.Margin = new Thickness(10, 0, 0, 0);
            cost.VerticalAlignment = VerticalAlignment.Top;
            ToolTip.SetTip(cost, "Souls");
            Grid.SetColumn(cost, 2);
            row.Children.Add(cost);
        }

        return new Border
        {
            Background = Brush(Palette.Mix(Palette.Surface2, shop, 0.32)),
            BorderBrush = Brush(shop),
            BorderThickness = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(7, 7, 0, 0),
            Padding = new Thickness(10, 10, 12, 10),
            Child = row,
        };
    }

    private static Border SectionStrip(TooltipSection section)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(Label(Format.Title(section.Kind), 13, Palette.TextDim, bold: true, italic: true));
        if (section.Cooldown.Length > 0)
        {
            var cooldown = Label($"{section.Cooldown} cooldown", 12, Palette.TextDim, bold: true);
            cooldown.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(cooldown, 1);
            row.Children.Add(cooldown);
        }
        return new Border { Background = Brush(Palette.Surface3), Padding = new Thickness(12, 4), Child = row };
    }

    private static void AddBlock(StackPanel body, TooltipBlock block, bool innate)
    {
        if (block.Text.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Inlines = TooltipMarkup.ToInlines(block.Text, Brush(Palette.TextDim)),
            });
        }

        foreach (var stat in block.Elevated)
            body.Children.Add(StatLine(stat, 17));

        if (block.Important.Count > 0)
        {
            var columns = Math.Min(3, block.Important.Count);
            var grid = new Grid();
            for (var column = 0; column < columns; column++)
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var rows = (block.Important.Count + columns - 1) / columns;
            for (var row = 0; row < rows; row++)
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var n = 0; n < block.Important.Count; n++)
            {
                var box = StatBox(block.Important[n]);
                box.Margin = new Thickness(n % columns == 0 ? 0 : 3, n / columns == 0 ? 0 : 6, n % columns == columns - 1 ? 0 : 3, 0);
                Grid.SetRow(box, n / columns);
                Grid.SetColumn(box, n % columns);
                grid.Children.Add(box);
            }
            body.Children.Add(grid);
        }

        if (block.Stats.Count == 0)
            return;
        if (innate)
        {
            // Innate stats are the item's plain bonuses: listed, not boxed.
            foreach (var stat in block.Stats)
                body.Children.Add(StatLine(stat, 13));
            return;
        }

        var lines = new StackPanel { Spacing = 3 };
        foreach (var stat in block.Stats)
            lines.Children.Add(StatLine(stat, 12));
        body.Children.Add(Box(lines, new Thickness(10, 6)));
    }

    private static Border Box(Control child, Thickness padding) => new()
    {
        Background = Brush(Palette.Surface2),
        BorderBrush = Brush(Palette.Border),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = padding,
        Child = child,
    };

    /// <summary>"+22%  Debuff Resist": the value bold, a drawback in red.</summary>
    private static TextBlock StatLine(TooltipStat stat, double size)
    {
        var inlines = new InlineCollection
        {
            new Run(stat.Value) { Foreground = Brush(stat.Negative ? Palette.Negative : Palette.Text), FontWeight = FontWeight.Bold },
            new Run("  " + stat.Label) { Foreground = Brush(Palette.TextDim) },
        };
        if (stat.Conditional)
            inlines.Add(new Run(" · conditional") { Foreground = Brush(Palette.TextFaint) });
        return new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, Inlines = inlines };
    }

    /// <summary>One of the headline numbers: value on top, what it is underneath.</summary>
    private static Border StatBox(TooltipStat stat)
    {
        var layout = new StackPanel { Spacing = 1 };
        if (stat.Value.Length > 0)
        {
            var value = Label(stat.Value, 18, stat.Negative ? Palette.Negative : Palette.Text, bold: true);
            value.HorizontalAlignment = HorizontalAlignment.Center;
            layout.Children.Add(value);
        }
        // A status effect (Silenced, Stun) has no number, so its name is the headline.
        var name = stat.Value.Length > 0
            ? Label(stat.Label, 12, Palette.TextDim, wrap: true)
            : Label(stat.Label, 15, Palette.Text, bold: true, wrap: true);
        name.TextAlignment = TextAlignment.Center;
        name.HorizontalAlignment = HorizontalAlignment.Center;
        layout.Children.Add(name);
        if (stat.Conditional)
        {
            var conditional = Label("Conditional", 11, Palette.TextFaint, italic: true);
            conditional.HorizontalAlignment = HorizontalAlignment.Center;
            layout.Children.Add(conditional);
        }
        return Box(layout, new Thickness(6));
    }

    private static StackPanel ItemLinks(DataStore store, string title, IReadOnlyList<string> itemIds)
    {
        var column = new StackPanel { Spacing = 4 };
        column.Children.Add(Label(title, 12, Palette.TextFaint, bold: true));
        foreach (var itemId in itemIds)
        {
            var item = store.Items[itemId];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new ArtImage
            {
                Kind = ArtKind.Item,
                ArtId = item.ItemId,
                ArtName = item.ItemName,
                Tint = Palette.ShopColor(item.Category),
                Width = 20,
                Height = 20,
            });
            var name = Label(item.ItemName, 13, Palette.Text, bold: true);
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            column.Children.Add(row);
        }
        return column;
    }
}
