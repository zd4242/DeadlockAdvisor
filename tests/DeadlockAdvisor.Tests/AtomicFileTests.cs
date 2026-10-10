using System.Text;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

/// <summary>Writing through a temporary file, including when something else has the target open for a moment.</summary>
public class AtomicFileTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task ReplacesTheFileAndLeavesNoTemporaryOne()
    {
        using var folder = new TempDirectory();
        var path = folder.File("a.txt");
        await File.WriteAllTextAsync(path, "old");

        await AtomicFile.WriteAsync(path, stream => stream.WriteAsync(Bytes("new")).AsTask());

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(folder.Path));
    }

    /// <summary>A virus scanner or an editor can have the file open as the swap comes; once it lets go the write goes through.</summary>
    [Fact]
    public async Task AFileHeldOpenForAMomentIsReplacedOnceItIsLetGo()
    {
        using var folder = new TempDirectory();
        var path = folder.File("a.txt");
        await File.WriteAllTextAsync(path, "old");
        var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var writing = AtomicFile.WriteAsync(path, stream => stream.WriteAsync(Bytes("new")).AsTask());

        await Task.Delay(150);
        Assert.False(writing.IsCompleted);
        hold.Dispose();
        await writing;

        Assert.Equal("new", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void TheSynchronousWriteWaitsOutAHoldToo()
    {
        using var folder = new TempDirectory();
        var path = folder.File("a.txt");
        File.WriteAllText(path, "old");
        var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            hold.Dispose();
        });

        AtomicFile.Write(path, Bytes("new"));

        Assert.Equal("new", File.ReadAllText(path));
    }

    /// <summary>Held for good, it fails after the retries, with the old contents intact and no temporary file left.</summary>
    [Fact]
    public async Task AFileHeldForGoodFailsLeavingTheOldContents()
    {
        using var folder = new TempDirectory();
        var path = folder.File("a.txt");
        await File.WriteAllTextAsync(path, "old");
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Windows says "access denied" for a file that is open elsewhere.
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => AtomicFile.WriteAsync(path, stream => stream.WriteAsync(Bytes("new")).AsTask()));
        Assert.True(failure is IOException or UnauthorizedAccessException);

        Assert.Equal("old", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task AMissingFolderFailsAtOnceRatherThanRetrying()
    {
        using var folder = new TempDirectory();
        var path = Path.Combine(folder.Path, "gone", "a.txt");
        var started = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<IOException>(() => AtomicFile.WriteAsync(path, stream => stream.WriteAsync(Bytes("new")).AsTask()));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }
}
