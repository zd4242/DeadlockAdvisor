using System.IO;

namespace DeadlockAdvisor.Services;

/// <summary>
/// Writes through a temporary sibling file that replaces the target only once it is complete,
/// so a crash or failure mid-write leaves the previous contents intact instead of a truncated file.
/// </summary>
public static class AtomicFile
{
    private const string _tempExtension = ".tmp";

    public static async Task WriteAsync(string path, Func<Stream, Task> write)
    {
        var tempPath = path + _tempExtension;

        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await write(stream);
            }

            File.Move(tempPath, path, overwrite: true);
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

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

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
