using System.IO;
using System.Numerics;

namespace DeadlockAdvisor.Vision;

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
    public TemplateBank(IReadOnlyList<string> heroes, IReadOnlyList<int> rowsHero, IReadOnlyList<float[]> vectors, IReadOnlyList<string> sources)
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
    public IReadOnlyList<string> Sources { get; }

    public bool IsEmpty => Heroes.Count == 0 || Vectors.Count == 0;

    /// <summary>A descriptor → the best score per hero (dot product with their best variant).</summary>
    public float[] Scores(float[] descriptor)
    {
        var scores = new float[Heroes.Count];
        Array.Fill(scores, float.NegativeInfinity);
        for (var row = 0; row < Vectors.Count; row++)
        {
            var value = Dot(descriptor, Vectors[row]);
            var hero = RowsHero[row];
            if (value > scores[hero])
                scores[hero] = value;
        }
        return scores;
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
    private static SortedDictionary<string, List<string>> VariantFiles(string directory)
    {
        var found = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        if (!Directory.Exists(directory))
            return found;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(LowerName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);
            if (name.StartsWith('_'))
                continue;
            if (File.Exists(entry) && IsImage(entry))
            {
                Add(Path.GetFileNameWithoutExtension(entry), entry);
            }
            else if (Directory.Exists(entry))
            {
                foreach (var variant in Directory.EnumerateFiles(entry).Where(IsImage).OrderBy(LowerName, StringComparer.Ordinal))
                    Add(name, variant);
            }
        }
        return found;

        void Add(string heroId, string path)
        {
            if (!found.TryGetValue(heroId, out var list))
                found[heroId] = list = [];
            list.Add(path);
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
        var sources = new List<string>();
        foreach (var (heroId, paths) in files)
        {
            var added = false;
            foreach (var path in paths)
            {
                float[]? vector;
                try
                {
                    vector = ImageOps.Descriptor(ImageFile.Load(path));
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
                sources.Add(path);
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
