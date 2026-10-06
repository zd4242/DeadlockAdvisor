using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

public class ArtService(ILoggingService loggingService) : IArtService
{
    private static readonly string[] _suffixes = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    private readonly Dictionary<ArtKind, Dictionary<string, string>> _index = [];
    private readonly Dictionary<(ArtKind Kind, string Id), Bitmap?> _sources = [];
    private readonly Dictionary<(ArtKind Kind, string Id, int Size), Bitmap> _scaled = [];

    public string AssetsDir { get; private set; } = "";

    public void SetAssetsDir(string assetsDir)
    {
        AssetsDir = assetsDir;
        Refresh();
    }

    public void Refresh()
    {
        _index.Clear();
        _sources.Clear();
        _scaled.Clear();
    }

    public string FolderOf(ArtKind kind) => Path.Combine(AssetsDir, kind switch
    {
        ArtKind.Hero => "heroes",
        ArtKind.Rank => "ranks",
        _ => "items",
    });

    public int Count(ArtKind kind) => Index(kind).Count;

    public bool Has(ArtKind kind, string id) => Index(kind).ContainsKey(id.ToLowerInvariant());

    public Bitmap? Get(ArtKind kind, string id, int pixelSize)
    {
        if (pixelSize <= 0)
            return null;
        if (_scaled.TryGetValue((kind, id, pixelSize), out var cached))
            return cached;

        var source = Source(kind, id);
        if (source is null)
            return null;

        var width = source.PixelSize.Width;
        var height = source.PixelSize.Height;
        Rect from;
        Rect to;
        if (kind == ArtKind.Rank)
        {
            // A badge is an emblem wider than tall on a clear background: fitted whole, not cropped to a square.
            var scale = Math.Min((double)pixelSize / width, (double)pixelSize / height);
            from = new Rect(0, 0, width, height);
            to = new Rect((pixelSize - width * scale) / 2, (pixelSize - height * scale) / 2, width * scale, height * scale);
        }
        else
        {
            // Cover-crop to a square so art of any aspect ratio lines up. Hero art is a tall character
            // card, so its square comes from a quarter of the way down rather than the middle.
            var side = Math.Min(width, height);
            var x = (width - side) / 2;
            var y = Math.Round((height - side) * (kind == ArtKind.Hero ? 0.25 : 0.5));
            from = new Rect(x, y, side, side);
            to = new Rect(0, 0, pixelSize, pixelSize);
        }

        var target = new RenderTargetBitmap(new PixelSize(pixelSize, pixelSize), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
        {
            context.DrawImage(source, from, to);
        }
        _scaled[(kind, id, pixelSize)] = target;
        return target;
    }

    private Bitmap? Source(ArtKind kind, string id)
    {
        if (_sources.TryGetValue((kind, id), out var cached))
            return cached;

        Bitmap? bitmap = null;
        if (Index(kind).TryGetValue(id.ToLowerInvariant(), out var path))
        {
            try
            {
                bitmap = new Bitmap(path);
            }
            catch (Exception ex)
            {
                loggingService.Warning($"Couldn't read art {path}: {ex.Message}");
            }
        }
        _sources[(kind, id)] = bitmap;
        return bitmap;
    }

    /// <summary>Lowercased file stem → path; the first of the known suffixes wins when a stem has several.</summary>
    private Dictionary<string, string> Index(ArtKind kind)
    {
        if (_index.TryGetValue(kind, out var index))
            return index;

        index = [];
        var folder = FolderOf(kind);
        if (Directory.Exists(folder))
        {
            var files = Directory.EnumerateFiles(folder).ToList();
            foreach (var suffix in _suffixes)
            {
                foreach (var file in files.Where(f => string.Equals(Path.GetExtension(f), suffix, StringComparison.OrdinalIgnoreCase)))
                    index.TryAdd(Path.GetFileNameWithoutExtension(file).ToLowerInvariant(), file);
            }
        }
        _index[kind] = index;
        return index;
    }
}
