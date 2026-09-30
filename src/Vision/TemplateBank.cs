using System.IO;
using System.Numerics;

namespace DeadlockAdvisor.Vision;

/// <summary>Where a reference image came from, which decides how far it's trusted and when it's used.</summary>
public enum TemplateKind
{
    /// <summary>The API's top-bar art, assets/topbar/&lt;hero&gt;.png.</summary>
    Api,

    /// <summary>In-game portraits shipped with the app for art the API had wrong.</summary>
    Bundled,

    /// <summary>Cut from the API's hero cards to the top bar's framing.</summary>
    Derived,

    /// <summary>Crops the user corrected in the review, and anything else put in a hero's folder.</summary>
    Learned,
}

/// <summary>Which portrait the game is drawing: its art changes on low health and on a kill streak.</summary>
public enum PortraitState
{
    Normal,
    Critical,
    Gloat,
}

public sealed record TemplateSource(string Hero, string Path, TemplateKind Kind, PortraitState State = PortraitState.Normal)
{
    /// <summary>By file name: a hero's own &lt;hero&gt;.png is the API's, and the prefix says what an alternate is.</summary>
    public static TemplateSource Of(string hero, string path, bool isAlternate)
    {
        if (!isAlternate)
            return new(hero, path, TemplateKind.Api);
        var name = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return name switch
        {
            _ when name.StartsWith("bundled_", StringComparison.Ordinal) || name.StartsWith("ingame_", StringComparison.Ordinal)
                => new(hero, path, TemplateKind.Bundled),
            _ when name.StartsWith("card_", StringComparison.Ordinal) => new(hero, path, TemplateKind.Derived),
            _ when name.StartsWith("state_critical", StringComparison.Ordinal) => new(hero, path, TemplateKind.Derived, PortraitState.Critical),
            _ when name.StartsWith("state_gloat", StringComparison.Ordinal) => new(hero, path, TemplateKind.Derived, PortraitState.Gloat),
            _ => new(hero, path, TemplateKind.Learned),
        };
    }
}

/// <summary>
/// The reference art detection matches against: assets/topbar/&lt;hero_id&gt;.png, the art Deadlock
/// draws in its scoreboard strip, plus any number of alternates in assets/topbar/&lt;hero_id&gt;/. Every
/// variant is scored and a hero takes their best, which is how alternate portraits are handled
/// without characterising them up front: a corrected misread saved as a variant stops recurring.
/// <para>
/// Descriptors are rebuilt on every load, in milliseconds; the Python app's _templates.npz cache is
/// left for the Python app.
/// </para>
/// </summary>
public sealed class TemplateBank
{
    public const string PythonCacheName = "_templates.npz";

    /// <param name="rowsHero">Each vector's hero, as an index into <paramref name="heroes"/>; rows are grouped by hero, in hero order.</param>
    public TemplateBank(IReadOnlyList<string> heroes, IReadOnlyList<int> rowsHero, IReadOnlyList<float[]> vectors,
        IReadOnlyList<TemplateSource> sources)
    {
        Heroes = heroes;
        RowsHero = rowsHero;
        Vectors = vectors;
        Sources = sources;
    }

    public static TemplateBank Empty { get; } = new([], [], [], []);

    public IReadOnlyList<string> Heroes { get; }
    public IReadOnlyList<int> RowsHero { get; }
    public IReadOnlyList<float[]> Vectors { get; }
    public IReadOnlyList<TemplateSource> Sources { get; }

    public bool IsEmpty => Heroes.Count == 0 || Vectors.Count == 0;

    /// <summary>A descriptor → the best score per hero (dot product with their best variant).</summary>
    public float[] Scores(float[] descriptor) => Scores(descriptor, null);

    /// <summary>As <see cref="Scores(float[])"/>, also noting which row gave each hero their best.</summary>
    public float[] Scores(float[] descriptor, int[]? bestRows)
    {
        var scores = new float[Heroes.Count];
        Array.Fill(scores, float.NegativeInfinity);
        for (var row = 0; row < Vectors.Count; row++)
        {
            var value = Dot(descriptor, Vectors[row]);
            var hero = RowsHero[row];
            if (value > scores[hero])
            {
                scores[hero] = value;
                if (bestRows is not null)
                    bestRows[hero] = row;
            }
        }
        return scores;
    }

    /// <summary>
    /// Only the images <paramref name="include"/> keeps, as a bank of its own. A hero left with none
    /// drops out, as one whose every image failed to load does.
    /// </summary>
    public TemplateBank Where(Func<TemplateSource, bool> include)
    {
        var heroes = new List<string>();
        var rowsHero = new List<int>();
        var vectors = new List<float[]>();
        var sources = new List<TemplateSource>();
        for (var row = 0; row < Vectors.Count; row++)
        {
            if (!include(Sources[row]))
                continue;
            if (heroes.Count == 0 || heroes[^1] != Heroes[RowsHero[row]])
                heroes.Add(Heroes[RowsHero[row]]);
            rowsHero.Add(heroes.Count - 1);
            vectors.Add(Vectors[row]);
            sources.Add(Sources[row]);
        }
        return new TemplateBank(heroes, rowsHero, vectors, sources);
    }

    private static float Dot(float[] a, float[] b)
    {
        var width = Vector<float>.Count;
        var sum = Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - width; i += width)
            sum += new Vector<float>(a, i) * new Vector<float>(b, i);
        var total = System.Numerics.Vector.Sum(sum);
        for (; i < a.Length; i++)
            total += a[i] * b[i];
        return total;
    }

    /// <summary>hero_id → every image that can stand in for them, sorted the way Python's sorted(iterdir()) is.</summary>
    private static SortedDictionary<string, List<TemplateSource>> VariantFiles(string directory)
    {
        var found = new SortedDictionary<string, List<TemplateSource>>(StringComparer.Ordinal);
        if (!Directory.Exists(directory))
            return found;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(LowerName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);
            if (name.StartsWith('_'))
                continue;
            if (File.Exists(entry) && IsImage(entry))
            {
                var heroId = Path.GetFileNameWithoutExtension(entry);
                Add(TemplateSource.Of(heroId, entry, isAlternate: false));
            }
            else if (Directory.Exists(entry))
            {
                foreach (var variant in Directory.EnumerateFiles(entry).Where(IsImage).OrderBy(LowerName, StringComparer.Ordinal))
                    Add(TemplateSource.Of(name, variant, isAlternate: true));
            }
        }
        return found;

        void Add(TemplateSource source)
        {
            if (!found.TryGetValue(source.Hero, out var list))
                found[source.Hero] = list = [];
            list.Add(source);
        }
    }

    // Windows paths compare case-insensitively in Python's pathlib, by their lowercased names.
    private static string LowerName(string path) => Path.GetFileName(path).ToLowerInvariant();

    private static bool IsImage(string path) => ImageFile.Suffixes.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static TemplateBank Load(string directory)
    {
        var files = VariantFiles(directory);
        var heroes = new List<string>();
        var rowsHero = new List<int>();
        var vectors = new List<float[]>();
        var sources = new List<TemplateSource>();
        foreach (var (heroId, candidates) in files)
        {
            var added = false;
            foreach (var source in candidates)
            {
                float[]? vector;
                try
                {
                    vector = ImageOps.Descriptor(ImageFile.Load(source.Path));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (vector is null)
                    continue;
                // A hero whose every image failed to load mustn't keep an empty column.
                if (!added)
                    heroes.Add(heroId);
                added = true;
                rowsHero.Add(heroes.Count - 1);
                vectors.Add(vector);
                sources.Add(source);
            }
        }
        return new TemplateBank(heroes, rowsHero, vectors, sources);
    }

    /// <summary>
    /// Write a corrected crop into the hero's variant folder, from the review modal, so today's
    /// misread becomes tomorrow's reference. Drops the Python app's descriptor cache so it rebuilds too.
    /// </summary>
    public static string SaveVariant(string directory, string heroId, RgbImage image, string label = "variant")
    {
        var folder = Path.Combine(directory, heroId);
        Directory.CreateDirectory(folder);
        var index = 1;
        while (File.Exists(Path.Combine(folder, $"{label}_{index:00}.png")))
            index++;
        var path = Path.Combine(folder, $"{label}_{index:00}.png");
        Png.Save(image, path);
        InvalidatePythonCache(directory);
        return path;
    }

    /// <summary>Drop the Python app's descriptor cache after adding art, so it rebuilds from what's on disk.</summary>
    public static void InvalidatePythonCache(string directory)
    {
        var cache = Path.Combine(directory, PythonCacheName);
        try
        {
            if (File.Exists(cache))
                File.Delete(cache);
        }
        catch (IOException)
        {
        }
    }
}
