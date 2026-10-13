using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Tests.Support;

/// <summary>The item art and labelled boards in Golden/vision/items.</summary>
public static class ItemData
{
    private static readonly Lazy<ItemTemplates> _templates = new(() => ItemTemplates.Load(ArtDir, Golden.LoadStore().Items.Values));

    /// <summary>Every item's icon, at 100 pixels (the art download's are 200): a cell is 36 pixels on a 1440p screen.</summary>
    public static string ArtDir => Golden.PathOf("vision", "items", "art");

    public static string CorpusDir => Golden.PathOf("vision", "items", "corpus");

    public static ItemTemplates Templates => _templates.Value;
}
