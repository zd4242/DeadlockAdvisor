using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Services.Formats;

namespace DeadlockAdvisor.Services;

/// <summary>What a published version changed, in its publisher's words: "Spirit items rate higher against Haze."</summary>
public sealed record ModelNote(string Published, string Text);

/// <summary>
/// The model: the hero ratings, item formulas and the game data they were tuned against, as the repo
/// publishes them in src/Assets/SeedData. A new install starts from the copy bundled in the app; existing
/// ones update from the repo's rolling "model" release, which CI publishes from main once the tests pass
/// (.github/workflows/ci.yml). model.json lists each file's SHA-256 (the tests keep it in step with the
/// files), and the copy written into a data folder records what was installed there, so a file changed
/// since, by hand or by a game sync, is told apart from one that's simply out of date.
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
    public const string ReleaseUrl = "https://github.com/zd4242/DeadlockAdvisor/releases/download/model";
    public const string ManifestUrl = $"{ReleaseUrl}/{FileName}";

    /// <summary>How many versions' notes model.json keeps, newest first.</summary>
    public const int MaxNotes = 20;

    /// <summary>What each version published so far changed, newest first; in a data folder's record, the notes already shown.</summary>
    public IReadOnlyList<ModelNote> Notes { get; init; } = [];

    /// <summary>The files a model covers, in the order they're listed. The match data has a download of its own.</summary>
    public static readonly IReadOnlyList<string> ModelFiles =
    [
        DataStore.CategoriesFile, DataStore.HeroScoresFile, DataStore.ItemCoefficientsFile, DataStore.TraitWeightsFile,
        DataStore.StatRulesFile, DataStore.HeroesFile, DataStore.ItemsFile, DataStore.ItemStatsFile, DataStore.ItemTooltipsFile,
    ];

    /// <summary>
    /// "trait_weights-1a2b3c4d.csv": a file as the release holds it, named by its contents, so a file the
    /// published model.json names never changes under it.
    /// </summary>
    public static string AssetOf(string file, string hash) => $"{Path.GetFileNameWithoutExtension(file)}-{hash[..8]}{Path.GetExtension(file)}";

    public string UrlOf(string file) => $"{ReleaseUrl}/{AssetOf(file, Files[file])}";

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The file's hash; null when it's missing.</summary>
    public static string? HashOf(string path) => File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null;

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
            if (Notes.Count > 0)
            {
                writer.WriteStartArray("notes");
                foreach (var note in Notes)
                {
                    writer.WriteStartObject();
                    writer.WriteString("published", note.Published);
                    writer.WriteString("text", note.Text);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
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
        var notes = root.TryGetProperty("notes", out var list)
            ? list.EnumerateArray().Select(note => new ModelNote(note.GetProperty("published").GetString() ?? "", note.GetProperty("text").GetString() ?? "")).ToList()
            : [];
        return new ModelManifest(root.GetProperty("format").GetInt32(), root.GetProperty("published").GetString() ?? "", Read("files"), Read("kept"))
        {
            Notes = notes,
        };
    }
}

/// <summary>What a published model would change in a data folder.</summary>
/// <param name="Current">Each model file's hash as the check found it; null when it's missing.</param>
/// <param name="Quiet">Out of date and unchanged since installed: replaced without asking.</param>
/// <param name="Edited">Changed since installed, by hand or by a game sync, with a new version published: its owner is asked.</param>
public sealed record ModelUpdatePlan(ModelManifest Published, ModelManifest? Installed, IReadOnlyDictionary<string, string?> Current,
    IReadOnlyList<string> Quiet, IReadOnlyList<string> Edited)
{
    /// <summary>How many of the notes not yet shown an update shows: a folder from long ago doesn't get the whole history.</summary>
    public const int MaxNewsShown = 5;

    public bool HasWork => Quiet.Count > 0 || Edited.Count > 0;

    /// <summary>
    /// A changed heroes.csv with a newer one published: never asked about, since the heroes it lacks are added
    /// to it (<see cref="ModelUpdateService.AddNewHeroes"/>) and the rest is its owner's. Recorded as taken.
    /// </summary>
    public IReadOnlyList<string> Additive { get; init; } = [];

    /// <summary>What's changed since the version installed here, newest first, as the update says it.</summary>
    public IReadOnlyList<ModelNote> News => Published.Notes.Except(Installed?.Notes ?? []).Take(MaxNewsShown).ToList();

    /// <param name="askAgain">Ask about edited files even where this version was turned down before, as a check on demand does.</param>
    public static ModelUpdatePlan For(ModelManifest published, string dataDir, bool askAgain)
    {
        var installed = ModelManifest.Installed(dataDir);
        var current = new Dictionary<string, string?>();
        var quiet = new List<string>();
        var edited = new List<string>();
        var additive = new List<string>();
        foreach (var file in ModelManifest.ModelFiles.Where(published.Files.ContainsKey))
        {
            var now = ModelManifest.HashOf(Path.Combine(dataDir, file));
            current[file] = now;
            var latest = published.Files[file];
            var before = installed?.Files.GetValueOrDefault(file);
            // Changed here, but nothing newer is published than what was installed: nothing to ask about.
            if (now == latest || latest == before)
                continue;
            if (now is null || now == before)
                quiet.Add(file);
            else if (file == DataStore.HeroesFile)
                additive.Add(file);
            else if (askAgain || installed?.Kept.GetValueOrDefault(file) != latest)
                edited.Add(file);
        }
        return new ModelUpdatePlan(published, installed, current, quiet, edited) { Additive = additive };
    }

    /// <summary>
    /// Putting files back to <paramref name="published"/>: every model file here that differs from it, whatever
    /// made it differ (an edit, Sync from Game API, an update undone), for its owner to tick.
    /// </summary>
    public static ModelUpdatePlan Reset(ModelManifest published, string dataDir)
    {
        var current = ModelManifest.ModelFiles.Where(published.Files.ContainsKey)
            .ToDictionary(file => file, file => ModelManifest.HashOf(Path.Combine(dataDir, file)));
        var differing = current.Where(pair => pair.Value != published.Files[pair.Key]).Select(pair => pair.Key).ToList();
        return new ModelUpdatePlan(published, ModelManifest.Installed(dataDir), current, [], differing);
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
            if (replaced.Contains(file) || Additive.Contains(file) || Current.GetValueOrDefault(file) == latest)
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
        return new ModelManifest(ModelManifest.CurrentFormat, Published.Published, files, kept) { Notes = Published.Notes };
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
            var published = ModelManifest.Parse(await api.GetBytesAsync(ModelManifest.ManifestUrl, DeadlockApi.UserAgent, cancellationToken));
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
            var bytes = await api.GetBytesAsync(published.UrlOf(file), DeadlockApi.UserAgent, cancellationToken);
            if (ModelManifest.Hash(bytes) != published.Files[file])
                throw new InvalidDataException($"The published {ModelManifest.Title(file).ToLowerInvariant()} ({file}) didn't arrive intact.");
            downloaded[file] = bytes;
        }
        return downloaded;
    }

    /// <summary>Where an update keeps the files it replaced, until the next one, so it can be undone (<see cref="Undo"/>).</summary>
    public const string PreviousFolderName = ".model-previous";

    /// <summary>
    /// Write the downloaded files into <paramref name="dataDir"/>, each keeping a backup of the one it replaces,
    /// and the record of what's installed. The files replaced are also kept in <see cref="PreviousFolderName"/>,
    /// in place of an earlier update's. A file changed since the check (an edit saved meanwhile) is left as it
    /// is. Returns the files written.
    /// </summary>
    public static IReadOnlySet<string> Install(string dataDir, ModelUpdatePlan update, IReadOnlyDictionary<string, byte[]> downloaded)
    {
        var replacing = new Dictionary<string, byte[]?>();
        foreach (var file in downloaded.Keys)
        {
            var path = Path.Combine(dataDir, file);
            var old = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if ((old is null ? null : ModelManifest.Hash(old)) == update.Current.GetValueOrDefault(file))
                replacing[file] = old;
        }
        if (replacing.Count > 0)
        {
            var previous = Path.Combine(dataDir, PreviousFolderName);
            if (Directory.Exists(previous))
                Directory.Delete(previous, recursive: true);
            Directory.CreateDirectory(previous);
            foreach (var (file, old) in replacing)
            {
                if (old is not null)
                    File.WriteAllBytes(Path.Combine(previous, file), old);
            }
        }
        foreach (var file in replacing.Keys)
            BackedUpFile.Write(Path.Combine(dataDir, file), downloaded[file]);
        var written = replacing.Keys.ToHashSet();
        Record(dataDir, update.Record(written));
        return written;
    }

    /// <summary>The files the last update replaced that are still as it left them, so undoing it loses nothing.</summary>
    public static IReadOnlyList<string> Undoable(string dataDir)
    {
        var previous = Path.Combine(dataDir, PreviousFolderName);
        if (ModelManifest.Installed(dataDir) is not { } installed || !Directory.Exists(previous))
            return [];
        return ModelManifest.ModelFiles
            .Where(file => File.Exists(Path.Combine(previous, file))
                           && installed.Files.GetValueOrDefault(file) is { } hash
                           && ModelManifest.HashOf(Path.Combine(dataDir, file)) == hash)
            .ToList();
    }

    /// <summary>
    /// Put back the files the last update replaced (<see cref="Undoable"/>), each keeping a backup of what the
    /// update wrote. They're recorded as kept over that version, so it isn't offered again; a newer one asks.
    /// Returns the files put back.
    /// </summary>
    public static IReadOnlyList<string> Undo(string dataDir)
    {
        var files = Undoable(dataDir);
        if (files.Count == 0 || ModelManifest.Installed(dataDir) is not { } installed)
            return [];
        var previous = Path.Combine(dataDir, PreviousFolderName);
        foreach (var file in files)
            BackedUpFile.Write(Path.Combine(dataDir, file), File.ReadAllBytes(Path.Combine(previous, file)));
        var kept = installed.Kept.ToDictionary();
        foreach (var file in files)
            kept[file] = installed.Files[file];
        Record(dataDir, installed with { Kept = kept });
        Directory.Delete(previous, recursive: true);
        return files;
    }

    /// <summary>Write a data folder's record, unless it already says the same.</summary>
    public static void Record(string dataDir, ModelManifest record)
    {
        var path = Path.Combine(dataDir, ModelManifest.FileName);
        var bytes = record.ToJsonBytes();
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            AtomicFile.Write(path, bytes);
    }

    // -- new heroes ---------------------------------------------------------------------

    /// <summary>The files a hero lives in: its row, and its trait ratings.</summary>
    public static readonly IReadOnlyList<string> HeroFiles = [DataStore.HeroesFile, DataStore.HeroScoresFile];

    /// <summary>
    /// The hero files <see cref="AddNewHeroes"/> should add to: changed here, with a newer version published,
    /// which would otherwise be asked about (or, for heroes.csv, left out). One the owner kept over this version
    /// isn't offered again, unless <paramref name="askAgain"/>. A file nobody changed is replaced whole, which
    /// brings the heroes with it.
    /// </summary>
    public static IReadOnlyList<string> HeroFilesToMerge(ModelManifest published, string dataDir, bool askAgain)
    {
        var installed = ModelManifest.Installed(dataDir);
        return HeroFiles.Where(file =>
        {
            var before = installed?.Files.GetValueOrDefault(file);
            return published.Files.TryGetValue(file, out var latest)
                   && ModelManifest.HashOf(Path.Combine(dataDir, file)) is { } now
                   && now != latest && now != before && latest != before
                   && (askAgain || installed?.Kept.GetValueOrDefault(file) != latest);
        }).ToList();
    }

    /// <summary>
    /// Add the heroes of the published model that this folder lacks to <paramref name="files"/>, with their published
    /// trait ratings, leaving every row already there as it is: a hero the game releases shouldn't cost anyone a
    /// question, or make them choose between their own ratings and the new hero. A hero is the same one by hero id
    /// or by game id. Ratings are only taken for traits this folder has.
    /// </summary>
    /// <param name="files">The hero files to add to (<see cref="HeroFilesToMerge"/>).</param>
    /// <param name="published">The published <see cref="HeroFiles"/>, as downloaded.</param>
    public static HeroMerge AddNewHeroes(string dataDir, IReadOnlyCollection<string> files, IReadOnlyDictionary<string, byte[]> published)
    {
        var heroesPath = Path.Combine(dataDir, DataStore.HeroesFile);
        var ours = HeroRows(dataDir);
        var missing = MissingHeroes(ours, published);
        var written = new HashSet<string>();
        if (missing.Count == 0)
            return new HeroMerge([], written);

        var added = new List<string>();
        if (files.Contains(DataStore.HeroesFile))
        {
            var rows = ours.Concat(missing).Select(row => (IReadOnlyList<string>)[row.Required("hero_id"), row.Required("hero_name"), row.Get("game_id") ?? ""]);
            BackedUpFile.Write(heroesPath, CsvWriter.ToBytes(["hero_id", "hero_name", "game_id"], rows));
            written.Add(DataStore.HeroesFile);
            added.AddRange(missing.Select(row => row.Required("hero_name")));
        }
        if (files.Contains(DataStore.HeroScoresFile) && AddNewHeroRatings(dataDir, missing.Select(row => row.Required("hero_id")).ToHashSet(StringComparer.Ordinal), published))
            written.Add(DataStore.HeroScoresFile);
        return new HeroMerge(added, written);
    }

    /// <summary>The heroes of the published model this folder lacks, as it names them, to offer them to someone who isn't taking updates.</summary>
    /// <param name="published">The published <see cref="DataStore.HeroesFile"/>, as downloaded.</param>
    public static IReadOnlyList<string> NewHeroNames(string dataDir, IReadOnlyDictionary<string, byte[]> published) =>
        MissingHeroes(HeroRows(dataDir), published).Select(row => row.Required("hero_name")).ToList();

    private static List<CsvRow> HeroRows(string dataDir)
    {
        var path = Path.Combine(dataDir, DataStore.HeroesFile);
        return File.Exists(path) ? CsvReader.ReadFile(path).Where(row => row.Has("hero_id")).ToList() : [];
    }

    private static List<CsvRow> MissingHeroes(IReadOnlyList<CsvRow> ours, IReadOnlyDictionary<string, byte[]> published)
    {
        var ids = ours.Select(row => row.Required("hero_id")).ToHashSet(StringComparer.Ordinal);
        var gameIds = ours.Select(GameIdOf).Where(gameId => gameId != 0).ToHashSet();
        return Rows(published, DataStore.HeroesFile)
            .Where(row => row.Has("hero_id") && row.Has("hero_name") && !ids.Contains(row.Required("hero_id"))
                          && !(GameIdOf(row) is var gameId && gameId != 0 && gameIds.Contains(gameId)))
            .ToList();
    }

    /// <summary>The new heroes' published rows appended to this folder's ratings, for the traits it has. Whether any were.</summary>
    private static bool AddNewHeroRatings(string dataDir, HashSet<string> heroIds, IReadOnlyDictionary<string, byte[]> published)
    {
        var scoresPath = Path.Combine(dataDir, DataStore.HeroScoresFile);
        var categoriesPath = Path.Combine(dataDir, DataStore.CategoriesFile);
        if (!File.Exists(scoresPath) || !File.Exists(categoriesPath))
            return false;
        var categories = CsvReader.ReadFile(categoriesPath).Where(row => row.Has("category_id")).Select(row => row.Required("category_id")).ToHashSet(StringComparer.Ordinal);
        var ours = CsvReader.ReadFile(scoresPath).Where(row => row.Has("hero_id") && row.Has("category_id")).ToList();
        var present = ours.Select(row => (row.Required("hero_id"), row.Required("category_id"))).ToHashSet();
        var extra = Rows(published, DataStore.HeroScoresFile)
            .Where(row => row.Has("hero_id") && row.Has("category_id") && heroIds.Contains(row.Required("hero_id"))
                          && categories.Contains(row.Required("category_id")) && !present.Contains((row.Required("hero_id"), row.Required("category_id"))))
            .ToList();
        if (extra.Count == 0)
            return false;
        var rows = ours.Concat(extra).Select(row => (IReadOnlyList<string>)[row.Required("hero_id"), row.Required("category_id"), row.Get("score") ?? ""]);
        BackedUpFile.Write(scoresPath, CsvWriter.ToBytes(["hero_id", "category_id", "score"], rows));
        return true;
    }

    private static List<CsvRow> Rows(IReadOnlyDictionary<string, byte[]> files, string file) =>
        files.TryGetValue(file, out var bytes) ? CsvReader.Read(Encoding.UTF8.GetString(bytes)) : [];

    private static long GameIdOf(CsvRow row) =>
        long.TryParse(row.Get("game_id"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var gameId) ? gameId : 0;
}

/// <summary>What <see cref="ModelUpdateService.AddNewHeroes"/> did.</summary>
/// <param name="Added">The heroes added to heroes.csv, as the published model names them.</param>
/// <param name="Written">The hero files it wrote.</param>
public sealed record HeroMerge(IReadOnlyList<string> Added, IReadOnlySet<string> Written);
