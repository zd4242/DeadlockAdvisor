using System.IO;
using System.Threading;
using DeadlockAdvisor.Models;

namespace DeadlockAdvisor.Vision.Items;

/// <param name="Art">The icon over a flat grey, which the game draws it over too.</param>
public sealed record ItemIcon(string ItemId, ShopCategory Category, int Tier, int Cost, RgbImage Art);

/// <summary>
/// Every item's icon as the game draws it in a cell, ready to compare with one: shrunk to the cell's own pixels, then framed
/// (<see cref="ItemFrame"/>). The art is <c>assets/items/&lt;item_id&gt;.png</c>; anything in <c>assets/items/&lt;item_id&gt;/</c> is
/// another way the game draws that item (a dark Tankbuster where the API's is cream) and is matched too, as a hero's
/// learned portraits are.
/// </summary>
public sealed class ItemTemplates
{
    /// <summary>Bumped when the framing changes.</summary>
    public const int Version = 1;

    private static readonly (byte R, byte G, byte B) _under = (40, 40, 40);

    private readonly List<ItemIcon> _icons;
    private readonly Dictionary<int, float[][]> _frames = [];
    private readonly Lock _lock = new();

    public ItemTemplates(IEnumerable<ItemIcon> icons)
    {
        _icons = [.. icons];
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new List<ItemIcon>();
        var itemOf = new int[_icons.Count];
        for (var i = 0; i < _icons.Count; i++)
        {
            if (!index.TryGetValue(_icons[i].ItemId, out var item))
            {
                index[_icons[i].ItemId] = item = items.Count;
                items.Add(_icons[i]);
            }
            itemOf[i] = item;
        }
        (Items, ItemOfIcon) = (items, itemOf);
        _itemIndex = index;
        _byCategory = Enum.GetValues<ShopCategory>().ToDictionary(
            category => category,
            category => Enumerable.Range(0, _icons.Count).Where(i => _icons[i].Category == category).ToArray());
    }

    private readonly Dictionary<ShopCategory, int[]> _byCategory;
    private readonly Dictionary<string, int> _itemIndex;

    /// <summary>Where an item is in <see cref="Items"/>, or null for one with no icon.</summary>
    public int? IndexOf(string itemId) => _itemIndex.TryGetValue(itemId, out var index) ? index : null;

    /// <summary>
    /// What an item of a category and tier costs: the middle cost among the items there are (they all cost the same, 800 doubling at
    /// each tier), or null for a tier none of them are at.
    /// </summary>
    public int? CostOf(ShopCategory category, int tier)
    {
        var costs = Items.Where(item => item.Category == category && item.Tier == tier).Select(item => item.Cost).Order().ToList();
        return costs.Count == 0 ? null : costs[costs.Count / 2];
    }

    /// <summary>The indices into <see cref="Icons"/> of one category's.</summary>
    public IReadOnlyList<int> IconsIn(ShopCategory category) => _byCategory[category];

    public static ItemTemplates Empty { get; } = new([]);

    /// <summary>One entry per icon and variant; an item can appear more than once.</summary>
    public IReadOnlyList<ItemIcon> Icons => _icons;

    /// <summary>Each item once, by its first icon.</summary>
    public IReadOnlyList<ItemIcon> Items { get; }

    /// <summary>For each icon, which of <see cref="Items"/> it is of.</summary>
    public IReadOnlyList<int> ItemOfIcon { get; }

    public bool IsEmpty => _icons.Count == 0;

    public static ShopCategory? CategoryOf(string name) => name.ToLowerInvariant() switch
    {
        "weapon" => ShopCategory.Weapon,
        "vitality" => ShopCategory.Vitality,
        "spirit" => ShopCategory.Spirit,
        _ => null,
    };

    /// <summary>The icons of <paramref name="items"/> that have art in <paramref name="directory"/> (an item without any isn't read, as one the art download hasn't reached yet).</summary>
    public static ItemTemplates Load(string directory, IEnumerable<Item> items)
    {
        var icons = new List<ItemIcon>();
        if (!Directory.Exists(directory))
            return Empty;
        foreach (var item in items)
        {
            if (CategoryOf(item.Category) is not { } category)
                continue;
            var files = new List<string>();
            if (File.Exists(Path.Combine(directory, item.ItemId + ".png")))
                files.Add(Path.Combine(directory, item.ItemId + ".png"));
            var variants = Path.Combine(directory, item.ItemId);
            if (Directory.Exists(variants))
                files.AddRange(Directory.EnumerateFiles(variants, "*.png").Order(StringComparer.Ordinal));
            foreach (var file in files)
            {
                try
                {
                    var (image, alpha) = ImageFile.LoadWithAlpha(file);
                    icons.Add(new ItemIcon(item.ItemId, category, item.Tier, item.Cost, ImageOps.Composite(image, alpha, _under)));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                }
            }
        }
        return new ItemTemplates(icons);
    }

    /// <summary>Every icon framed for a cell <paramref name="cellPixels"/> wide, in the order of <see cref="Icons"/>.</summary>
    public float[][] FramesAt(int cellPixels)
    {
        cellPixels = Math.Max(8, cellPixels);
        lock (_lock)
        {
            if (_frames.TryGetValue(cellPixels, out var kept))
                return kept;
            var frames = _icons.Select(icon =>
            {
                var small = ImageOps.ResizeRgb(icon.Art, cellPixels, cellPixels);
                return ItemFrame.Normalise(ItemFrame.Sample(small, cellPixels / 2.0, cellPixels / 2.0, cellPixels, ItemFrame.Size));
            }).ToArray();
            _frames[cellPixels] = frames;
            return frames;
        }
    }
}
