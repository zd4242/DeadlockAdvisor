using System.IO;
using DeadlockAdvisor.Scoring;

namespace DeadlockAdvisor.Services;

/// <summary>What publishing a data folder's model changed in the seed.</summary>
/// <param name="Files">The model files copied in, newer than the seed's.</param>
/// <param name="MatchData">The match lift was copied too: it's the starting point for an install that hasn't downloaded any.</param>
/// <param name="MatchDataSkipped">Why the match lift wasn't copied, when the folder's leans toward some ranks; null otherwise.</param>
/// <param name="Note">The note recorded with this version; null when none was given or there was no new version to record it with.</param>
public sealed record ModelPublished(IReadOnlyList<string> Files, bool MatchData, string? MatchDataSkipped, ModelNote? Note)
{
    public bool Changed => Files.Count > 0 || MatchData;
}

/// <summary>
/// Publishing the model from a data folder (tools/PublishModel): its model files copied into the seed
/// (src/Assets/SeedData), and the seed's model.json rewritten to list them, dated today. Pushed to main,
/// CI publishes that to the "model" release once the tests pass, and every install updates to it
/// (<see cref="ModelUpdateService"/>).
/// </summary>
public static class ModelPublisher
{
    /// <param name="published">"2026-10-02": the date the new version goes out under.</param>
    /// <param name="note">What this version changes, for the update to tell people; recorded only with a new version.</param>
    /// <exception cref="InvalidOperationException">The data folder doesn't load, so it isn't fit to publish.</exception>
    public static ModelPublished Publish(string dataDir, string seedDir, string published, string? note = null)
    {
        DataStore store;
        try
        {
            store = DataStore.Load(dataDir);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{dataDir} doesn't load, so it isn't published: {ex.Message}", ex);
        }

        var files = ModelManifest.ModelFiles.Where(file => CopyIfChanged(dataDir, seedDir, file)).ToList();

        // Only lifts over every match make a fair starting point: leaning ones would carry this folder's choice of ranks.
        string? skipped = null;
        var matchData = false;
        if (MatchStatsMath.RankOf(store.MatchMeta) is not null)
            skipped = "its match data leans toward some ranks; switch it to every rank first to publish it";
        else if (store.MatchLift.Count > 0)
            matchData = CopyIfChanged(dataDir, seedDir, DataStore.MatchLiftFile) | CopyIfChanged(dataDir, seedDir, DataStore.MatchMetaFile);

        // Rewritten when a file changed, or when it no longer matches the files (one copied in by hand).
        var manifestPath = Path.Combine(seedDir, ModelManifest.FileName);
        var listed = File.Exists(manifestPath) ? ModelManifest.Parse(File.ReadAllBytes(manifestPath)) : null;
        ModelNote? noted = null;
        if (files.Count > 0 || listed is null || !Listing(seedDir, listed).SequenceEqual(File.ReadAllBytes(manifestPath)))
        {
            var notes = listed?.Notes ?? [];
            if (!string.IsNullOrWhiteSpace(note))
            {
                noted = new ModelNote(published, note.Trim());
                notes = [noted, .. notes.Take(ModelManifest.MaxNotes - 1)];
            }
            File.WriteAllBytes(manifestPath, (ModelManifest.Of(seedDir, published) with { Notes = notes }).ToJsonBytes());
        }
        return new ModelPublished(files, matchData, skipped, noted);
    }

    /// <summary>What model.json would say of the files in <paramref name="seedDir"/> now, keeping <paramref name="listed"/>'s date and notes.</summary>
    public static byte[] Listing(string seedDir, ModelManifest listed) =>
        (ModelManifest.Of(seedDir, listed.Published) with { Notes = listed.Notes }).ToJsonBytes();

    /// <summary>
    /// The "model" release's files, as CI publishes them (.github/workflows/ci.yml): each model file under
    /// its content name (<see cref="ModelManifest.AssetOf"/>), and model.json. Returns their names.
    /// </summary>
    /// <exception cref="InvalidOperationException">A file isn't the one model.json lists, so the seed isn't fit to publish.</exception>
    public static IReadOnlyList<string> WriteAssets(string seedDir, string outDir)
    {
        var manifestPath = Path.Combine(seedDir, ModelManifest.FileName);
        var manifest = ModelManifest.Parse(File.ReadAllBytes(manifestPath));
        Directory.CreateDirectory(outDir);
        var names = new List<string>();
        foreach (var (file, hash) in manifest.Files)
        {
            var bytes = File.ReadAllBytes(Path.Combine(seedDir, file));
            if (ModelManifest.Hash(bytes) != hash)
                throw new InvalidOperationException($"{file} isn't the version {ModelManifest.FileName} lists: publish with tools/PublishModel.");
            var name = ModelManifest.AssetOf(file, hash);
            File.WriteAllBytes(Path.Combine(outDir, name), bytes);
            names.Add(name);
        }
        File.Copy(manifestPath, Path.Combine(outDir, ModelManifest.FileName), overwrite: true);
        names.Add(ModelManifest.FileName);
        return names;
    }

    private static bool CopyIfChanged(string fromDir, string toDir, string file)
    {
        var from = Path.Combine(fromDir, file);
        var to = Path.Combine(toDir, file);
        if (!File.Exists(from) || File.Exists(to) && File.ReadAllBytes(from).AsSpan().SequenceEqual(File.ReadAllBytes(to)))
            return false;
        File.Copy(from, to, overwrite: true);
        return true;
    }
}
