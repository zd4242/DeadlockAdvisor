using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

/// <summary>
/// The model: the hero ratings, item formulas and the game data they were tuned against, as the repo
/// publishes them in src/Assets/SeedData. A new install starts from the copy bundled in the app; existing
/// ones update from the repo itself. model.json lists each file's SHA-256 (the tests keep it in step with
/// the files), and the copy written into a data folder records what was installed there, so a file
/// changed since, by hand or by a game sync, is told apart from one that's simply out of date.
/// </summary>
/// <param name="Format">What the files hold; an app only takes a model of its own <see cref="CurrentFormat"/>.</param>
/// <param name="Published">"2026-10-02": when this version was published.</param>
/// <param name="Files">File name → SHA-256, lowercase hex.</param>
/// <param name="Kept">In a data folder's record: file name → the published version its owner chose to keep their own over.</param>
public sealed record ModelManifest(int Format, string Published, IReadOnlyDictionary<string, string> Files, IReadOnlyDictionary<string, string> Kept)
{
    /// <summary>Bump when a model file changes in a way an older app can't read, such as a new column it would drop.</summary>
    public const int CurrentFormat = 1;

    public const string FileName = "model.json";
    public const string BaseUrl = "https://raw.githubusercontent.com/zd4242/DeadlockAdvisor/main/src/Assets/SeedData";

    /// <summary>The files a model covers, in the order they're listed. The match data has a download of its own.</summary>
    public static readonly IReadOnlyList<string> ModelFiles =
    [
        DataStore.CategoriesFile, DataStore.HeroScoresFile, DataStore.ItemCoefficientsFile, DataStore.TraitWeightsFile,
        DataStore.StatRulesFile, DataStore.HeroesFile, DataStore.ItemsFile, DataStore.ItemStatsFile, DataStore.ItemTooltipsFile,
    ];

    public static string UrlOf(string file) => $"{BaseUrl}/{file}";

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>What the files in <paramref name="dir"/> are now, as a model published <paramref name="published"/>.</summary>
    public static ModelManifest Of(string dir, string published) =>
        new(CurrentFormat, published,
            ModelFiles.Where(file => File.Exists(Path.Combine(dir, file)))
                .ToDictionary(file => file, file => Hash(File.ReadAllBytes(Path.Combine(dir, file)))),
            new Dictionary<string, string>());

    /// <summary>"Hero trait ratings": a model file as the update dialog names it.</summary>
    public static string Title(string file) => file switch
    {
        DataStore.HeroScoresFile => "Hero trait ratings",
        DataStore.ItemCoefficientsFile => "Item formulas",
        DataStore.TraitWeightsFile => "Trait weights",
        DataStore.StatRulesFile => "Stat rules",
        DataStore.CategoriesFile => "Traits",
        DataStore.HeroesFile => "Heroes",
        DataStore.ItemsFile => "Items",
        DataStore.ItemStatsFile => "Item stats",
        DataStore.ItemTooltipsFile => "Item tooltips",
        _ => file,
    };

    /// <summary>The record in a data folder, or null when there's none (a folder from before models were published) or it's unreadable.</summary>
    public static ModelManifest? Installed(string dataDir)
    {
        var path = Path.Combine(dataDir, FileName);
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public byte[] ToJsonBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", Format);
            writer.WriteString("published", Published);
            WriteFiles(writer, "files", Files);
            if (Kept.Count > 0)
                WriteFiles(writer, "kept", Kept);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteFiles(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, string> files)
    {
        writer.WriteStartObject(name);
        foreach (var file in ModelFiles.Where(files.ContainsKey).Concat(files.Keys.Where(file => !ModelFiles.Contains(file)).Order(StringComparer.Ordinal)))
            writer.WriteString(file, files[file]);
        writer.WriteEndObject();
    }

    /// <summary>Throws a JSON / missing-property error on a malformed manifest.</summary>
    public static ModelManifest Parse(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Dictionary<string, string> Read(string name) =>
            root.TryGetProperty(name, out var files)
                ? files.EnumerateObject().ToDictionary(file => file.Name, file => file.Value.GetString() ?? "")
                : [];
        return new ModelManifest(root.GetProperty("format").GetInt32(), root.GetProperty("published").GetString() ?? "", Read("files"), Read("kept"));
    }
}

/// <summary>What a published model would change in a data folder.</summary>
/// <param name="Current">Each model file's hash as the check found it; null when it's missing.</param>
/// <param name="Quiet">Out of date and unchanged since installed: replaced without asking.</param>
/// <param name="Edited">Changed since installed, by hand or by a game sync, with a new version published: its owner is asked.</param>
public sealed record ModelUpdatePlan(ModelManifest Published, ModelManifest? Installed, IReadOnlyDictionary<string, string?> Current,
    IReadOnlyList<string> Quiet, IReadOnlyList<string> Edited)
{
    public bool HasWork => Quiet.Count > 0 || Edited.Count > 0;

    /// <param name="askAgain">Ask about edited files even where this version was turned down before, as a check on demand does.</param>
    public static ModelUpdatePlan For(ModelManifest published, string dataDir, bool askAgain)
    {
        var installed = ModelManifest.Installed(dataDir);
        var current = new Dictionary<string, string?>();
        var quiet = new List<string>();
        var edited = new List<string>();
        foreach (var file in ModelManifest.ModelFiles.Where(published.Files.ContainsKey))
        {
            var path = Path.Combine(dataDir, file);
            var now = File.Exists(path) ? ModelManifest.Hash(File.ReadAllBytes(path)) : null;
            current[file] = now;
            var latest = published.Files[file];
            if (now == latest)
                continue;
            if (now is null || now == installed?.Files.GetValueOrDefault(file))
                quiet.Add(file);
            else if (askAgain || installed?.Kept.GetValueOrDefault(file) != latest)
                edited.Add(file);
        }
        return new ModelUpdatePlan(published, installed, current, quiet, edited);
    }

    /// <summary>
    /// The data folder's record once <paramref name="replaced"/> are the published versions: those and the
    /// files already the same are recorded as installed; the edited ones not replaced keep their record,
    /// and are marked as kept over this version so they aren't asked about again.
    /// </summary>
    public ModelManifest Record(IReadOnlySet<string> replaced)
    {
        var files = new Dictionary<string, string>();
        var kept = Installed?.Kept.Where(pair => !replaced.Contains(pair.Key)).ToDictionary() ?? [];
        foreach (var (file, latest) in Published.Files)
        {
            if (replaced.Contains(file) || Current.GetValueOrDefault(file) == latest)
            {
                files[file] = latest;
                kept.Remove(file);
            }
            else if (Installed?.Files.GetValueOrDefault(file) is { } before)
            {
                files[file] = before;
            }
            if (Edited.Contains(file) && !replaced.Contains(file))
                kept[file] = latest;
        }
        return new ModelManifest(ModelManifest.CurrentFormat, Published.Published, files, kept);
    }
}

public interface IModelUpdateService
{
    /// <summary>The model the repo publishes now; null when it can't be reached or is of another format than this app reads.</summary>
    Task<ModelManifest?> PublishedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetch <paramref name="files"/> of <paramref name="published"/>, each checked against its hash. Writes nothing.
    /// Throws an HTTP / timeout error, or <see cref="InvalidDataException"/> for a file that didn't arrive intact.
    /// </summary>
    Task<IReadOnlyDictionary<string, byte[]>> DownloadAsync(ModelManifest published, IEnumerable<string> files, CancellationToken cancellationToken = default);
}

/// <summary>The model on GitHub (<see cref="ModelManifest"/>), and putting a downloaded one into a data folder.</summary>
public sealed class ModelUpdateService(IDeadlockApi api) : IModelUpdateService
{
    public async Task<ModelManifest?> PublishedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var published = ModelManifest.Parse(await api.GetBytesAsync(ModelManifest.UrlOf(ModelManifest.FileName), DeadlockApi.UserAgent, cancellationToken));
            return published.Format == ModelManifest.CurrentFormat ? published : null;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TimeoutException or JsonException or KeyNotFoundException
                                       or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, byte[]>> DownloadAsync(ModelManifest published, IEnumerable<string> files,
        CancellationToken cancellationToken = default)
    {
        var downloaded = new Dictionary<string, byte[]>();
        foreach (var file in files)
        {
            var bytes = await api.GetBytesAsync(ModelManifest.UrlOf(file), DeadlockApi.UserAgent, cancellationToken);
            if (ModelManifest.Hash(bytes) != published.Files[file])
                throw new InvalidDataException($"The published {ModelManifest.Title(file).ToLowerInvariant()} ({file}) didn't arrive intact.");
            downloaded[file] = bytes;
        }
        return downloaded;
    }

    /// <summary>
    /// Write the downloaded files into <paramref name="dataDir"/>, each keeping a backup of the one it replaces,
    /// and the record of what's installed. A file changed since the check (an edit saved meanwhile) is left as
    /// it is. Returns the files written.
    /// </summary>
    public static IReadOnlySet<string> Install(string dataDir, ModelUpdatePlan update, IReadOnlyDictionary<string, byte[]> downloaded)
    {
        var written = new HashSet<string>();
        foreach (var (file, bytes) in downloaded)
        {
            var path = Path.Combine(dataDir, file);
            var now = File.Exists(path) ? ModelManifest.Hash(File.ReadAllBytes(path)) : null;
            if (now != update.Current.GetValueOrDefault(file))
                continue;
            BackedUpFile.Write(path, bytes);
            written.Add(file);
        }
        Record(dataDir, update.Record(written));
        return written;
    }

    /// <summary>Write a data folder's record, unless it already says the same.</summary>
    public static void Record(string dataDir, ModelManifest record)
    {
        var path = Path.Combine(dataDir, ModelManifest.FileName);
        var bytes = record.ToJsonBytes();
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            AtomicFile.Write(path, bytes);
    }
}
