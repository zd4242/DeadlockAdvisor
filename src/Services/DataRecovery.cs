using System.IO;

namespace DeadlockAdvisor.Services;

/// <summary>
/// Loads the data folder the app is already using. A file that can't be read is kept as
/// <c>&lt;file&gt;.bad-&lt;yyyyMMdd-HHmmss&gt;</c> and replaced by the newest backup that loads, or by the bundled copy,
/// so one typo or a half-synced file doesn't stop the app starting.
/// </summary>
internal static class DataRecovery
{
    private const int _backupsTried = 10;
    private const int _damagedCopiesKept = 3;

    /// <returns>The store, and one sentence per replaced file for the user.</returns>
    public static (DataStore Store, List<string> Repairs) Load(string dataDir)
    {
        var repairs = new List<string>();
        var repaired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            try
            {
                return (DataStore.Load(dataDir), repairs);
            }
            catch (DataLoadException ex) when (repaired.Add(ex.File))
            {
                repairs.Add(Repair(dataDir, ex));
            }
        }
    }

    private static string Repair(string dataDir, DataLoadException failure)
    {
        var path = Path.Combine(dataDir, failure.File);
        var kept = $"{failure.File}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Move(path, Path.Combine(dataDir, kept), overwrite: true);
        DropOldCopies(dataDir, failure.File);

        var problem = $"{failure.File} couldn't be read ({failure.Reason}).";
        foreach (var (contents, source) in Replacements(path))
        {
            AtomicFile.Write(path, contents);
            if (Mends(dataDir, failure.File))
                return $"{problem} Restored from {source}; the damaged file is kept as {kept}.";
        }

        File.Delete(path);
        return $"{problem} It was set aside as {kept}, and the app carries on without it.";
    }

    /// <summary>Keeps the newest few damaged copies of a file, so a folder that keeps breaking one doesn't fill up.</summary>
    private static void DropOldCopies(string dataDir, string file)
    {
        var older = Directory.GetFiles(dataDir, file + ".bad-*")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(_damagedCopiesKept);
        foreach (var path in older)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The newest backups first, then the bundled copy (which is taken on trust: it's the last resort).</summary>
    private static IEnumerable<(byte[] Contents, string Source)> Replacements(string path)
    {
        foreach (var (file, time) in BackedUpFile.BackupsOf(path).Take(_backupsTried))
        {
            byte[] contents;
            try
            {
                contents = File.ReadAllBytes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            yield return (contents, $"the backup from {time:yyyy-MM-dd HH:mm}");
        }

        if (DataService.BundledCopy(Path.GetFileName(path)) is { } bundled)
            yield return (bundled, "the bundled copy");
    }

    /// <summary>Whether the folder gets past <paramref name="file"/> now; a different file failing is for the next round.</summary>
    private static bool Mends(string dataDir, string file)
    {
        try
        {
            DataStore.Load(dataDir);
            return true;
        }
        catch (DataLoadException ex)
        {
            return !string.Equals(ex.File, file, StringComparison.OrdinalIgnoreCase);
        }
    }
}
