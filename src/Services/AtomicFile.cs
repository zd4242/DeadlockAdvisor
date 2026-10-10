using System.IO;

namespace DeadlockAdvisor.Services;

/// <summary>
/// Writes through a temporary sibling file that replaces the target only once it is complete,
/// so a crash or failure mid-write leaves the previous contents intact instead of a truncated file.
/// </summary>
public static class AtomicFile
{
    private const string _tempExtension = ".tmp";

    // Something else (a virus scanner, the indexer, a backup, an editor) can hold the target open for a moment, and
    // Windows won't replace a file that's open. Those holds pass, so the swap is tried again a few times, over a second or so.
    private const int _swapAttempts = 8;
    private static readonly TimeSpan _swapRetryDelay = TimeSpan.FromMilliseconds(50);

    public static async Task WriteAsync(string path, Func<Stream, Task> write)
    {
        var tempPath = path + _tempExtension;

        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await write(stream);
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when (IsHold(ex) && attempt < _swapAttempts)
                {
                    await Task.Delay(_swapRetryDelay * attempt);
                }
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static void Write(string path, ReadOnlySpan<byte> contents)
    {
        var tempPath = path + _tempExtension;

        try
        {
            using (var stream = File.Create(tempPath))
            {
                stream.Write(contents);
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when (IsHold(ex) && attempt < _swapAttempts)
                {
                    System.Threading.Thread.Sleep(_swapRetryDelay * attempt);
                }
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>A failure to replace the file that waiting can cure: it's open elsewhere. A missing folder or file won't get better.</summary>
    private static bool IsHold(Exception ex) =>
        ex is UnauthorizedAccessException || ex is IOException and not (FileNotFoundException or DirectoryNotFoundException);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
