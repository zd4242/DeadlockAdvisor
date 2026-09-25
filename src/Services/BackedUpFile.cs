using System.Globalization;
using System.IO;

namespace DeadlockAdvisor.Services;

/// <summary>
/// The data files' save sequence: keep a timestamped copy of the old file under
/// <c>.backups/</c> (the newest <see cref="Keep"/> per file), then swap the new contents in
/// atomically, so an interrupted autosave can't leave a truncated file.
/// </summary>
public static class BackedUpFile
{
    public const int Keep = 12;
    public const string BackupFolderName = ".backups";

    public static void Write(string path, ReadOnlySpan<byte> contents)
    {
        Backup(path);
        AtomicFile.Write(path, contents);
    }

    private static void Backup(string path)
    {
        if (!File.Exists(path))
            return;

        var backupDir = Path.Combine(Path.GetDirectoryName(path)!, BackupFolderName);
        Directory.CreateDirectory(backupDir);

        var stem = Path.GetFileNameWithoutExtension(path);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dest = Path.Combine(backupDir, $"{stem}.{stamp}{Path.GetExtension(path)}");
        // Two saves in the same second keep the first copy, the older contents.
        if (!File.Exists(dest))
            File.Copy(path, dest);

        TrimBackups(backupDir, stem);
    }

    private static void TrimBackups(string backupDir, string stem)
    {
        // Matches the Python app's glob of "<stem>.*.csv".
        var existing = Directory.EnumerateFiles(backupDir)
            .Where(file =>
            {
                var name = Path.GetFileName(file);
                return name.Length >= stem.Length + 5
                       && name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)
                       && name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var old in existing.Take(Math.Max(0, existing.Count - Keep)))
        {
            try
            {
                File.Delete(old);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
