using Avalonia.Media.Imaging;

namespace DeadlockAdvisor.Services.Contracts;

public enum ArtKind
{
    Hero,
    Item,

    /// <summary>A rank's badge, named after its tier ("01" for Initiate): an emblem on a clear background, not a tile.</summary>
    Rank,
}

/// <summary>
/// Hero portraits, item icons and rank badges from assets/heroes, assets/items and assets/ranks, named after their ids.
/// Missing art isn't an error: callers draw a placeholder tile instead (see ArtPainter), or hide a rank's badge.
/// </summary>
public interface IArtService
{
    string AssetsDir { get; }

    void SetAssetsDir(string assetsDir);

    /// <summary>Forget the file index and every cached bitmap, to pick up art added while running.</summary>
    void Refresh();

    string FolderOf(ArtKind kind);
    int Count(ArtKind kind);
    bool Has(ArtKind kind, string id);

    /// <summary>
    /// The art cover-cropped to a square of <paramref name="pixelSize"/> physical pixels (hero art from
    /// near the top, where the face is), or null when there's no usable file. Cached per size.
    /// </summary>
    Bitmap? Get(ArtKind kind, string id, int pixelSize);
}
