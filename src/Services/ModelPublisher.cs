using System.IO;
using DeadlockAdvisor.Scoring;

namespace DeadlockAdvisor.Services;

/// <summary>What publishing a data folder's model changed in the seed.</summary>
/// <param name="Files">The model files copied in, newer than the seed's.</param>
/// <param name="MatchData">The match lift was copied too: it's the starting point for an install that hasn't downloaded any.</param>
/// <param name="MatchDataSkipped">Why the match lift wasn't copied, when the folder's leans toward some ranks; null otherwise.</param>
public sealed record ModelPublished(IReadOnlyList<string> Files, bool MatchData, string? MatchDataSkipped)
{
    public bool Changed => Files.Count > 0 || MatchData;
}

/// <summary>
/// Publishing the model from a data folder (tools/PublishModel): its model files copied into the seed
/// (src/Assets/SeedData), and the seed's model.json rewritten to list them, dated today. Pushed to main,
/// that's what every install updates to (<see cref="ModelUpdateService"/>).
/// </summary>
public static class ModelPublisher
{
    /// <param name="published">"2026-10-02": the date the new version goes out under.</param>
    /// <exception cref="InvalidOperationException">The data folder doesn't load, so it isn't fit to publish.</exception>
    public static ModelPublished Publish(string dataDir, string seedDir, string published)
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
        if (files.Count > 0 || listed is null || !ModelManifest.Of(seedDir, listed.Published).ToJsonBytes().SequenceEqual(File.ReadAllBytes(manifestPath)))
            File.WriteAllBytes(manifestPath, ModelManifest.Of(seedDir, published).ToJsonBytes());
        return new ModelPublished(files, matchData, skipped);
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
