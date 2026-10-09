using System.Globalization;
using System.IO;

namespace DeadlockAdvisor.Services;

/// <summary>
/// The data files' save sequence: keep a timestamped copy of the old file under
/// <c>.backups/</c>, then swap the new contents in atomically, so an interrupted autosave can't
/// leave a truncated file. Per file, the newest <see cref="Keep"/> copies stay, plus the newest
/// copy of each of the last <see cref="HourlyWindow"/> hours and of each of the last
/// <see cref="DailyWindow"/> days, so a mistake made after a long editing burst can still be undone.
/// </summary>
public static class BackedUpFile
{
    public const int Keep = 12;
    public static readonly TimeSpan HourlyWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan DailyWindow = TimeSpan.FromDays(14);
    public const string BackupFolderName = ".backups";

    private const string StampFormat = "yyyyMMdd-HHmmss";

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
        var extension = Path.GetExtension(path);
        var now = DateTime.Now;
        var dest = Path.Combine(backupDir, $"{stem}.{now.ToString(StampFormat, CultureInfo.InvariantCulture)}{extension}");
        // Two saves in the same second keep the first copy, the older contents.
        if (!File.Exists(dest))
            File.Copy(path, dest);

        TrimBackups(backupDir, stem, extension, now);
    }

    /// <summary>The backups of one file, newest first.</summary>
    internal static IReadOnlyList<(string File, DateTime Time)> BackupsOf(string path)
    {
        var backupDir = Path.Combine(Path.GetDirectoryName(path)!, BackupFolderName);
        if (!Directory.Exists(backupDir))
            return [];
        return Listed(backupDir, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path))
            .OrderByDescending(backup => backup.Time)
            .ToList();
    }

    private static List<(string File, DateTime Time)> Listed(string backupDir, string stem, string extension)
    {
        var backups = new List<(string File, DateTime Time)>();
        foreach (var file in Directory.EnumerateFiles(backupDir))
        {
            if (TryParseTime(Path.GetFileName(file), stem, extension, out var time))
                backups.Add((file, time));
        }
        return backups;
    }

    internal static void TrimBackups(string backupDir, string stem, string extension, DateTime now)
    {
        foreach (var old in Expired(Listed(backupDir, stem, extension), now))
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

    /// <summary>The backups no rule keeps.</summary>
    internal static IEnumerable<string> Expired(IReadOnlyList<(string File, DateTime Time)> backups, DateTime now)
    {
        var newestFirst = backups.OrderByDescending(backup => backup.Time).ToList();
        var kept = newestFirst.Take(Keep).Select(backup => backup.File).ToHashSet(StringComparer.OrdinalIgnoreCase);

        KeepNewestPer(newestFirst.Where(backup => now - backup.Time <= HourlyWindow),
            time => time.Date.AddHours(time.Hour));
        KeepNewestPer(newestFirst.Where(backup => now - backup.Time <= DailyWindow), time => time.Date);

        return newestFirst.Select(backup => backup.File).Where(file => !kept.Contains(file));

        void KeepNewestPer(IEnumerable<(string File, DateTime Time)> candidates, Func<DateTime, DateTime> bucket)
        {
            var seen = new HashSet<DateTime>();
            foreach (var candidate in candidates)
            {
                if (seen.Add(bucket(candidate.Time)))
                    kept.Add(candidate.File);
            }
        }
    }

    // Exactly "<stem>.<yyyyMMdd-HHmmss><extension>", so another file's backups ("item_stats." next to
    // "items.") and anything the user dropped in the folder are never matched.
    private static bool TryParseTime(string name, string stem, string extension, out DateTime time)
    {
        time = default;
        var prefix = stem.Length + 1;
        if (name.Length != prefix + StampFormat.Length + extension.Length
            || !name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            return false;

        return DateTime.TryParseExact(name.AsSpan(prefix, StampFormat.Length), StampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out time);
    }
}
