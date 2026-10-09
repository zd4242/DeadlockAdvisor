using DeadlockAdvisor.Core;

namespace DeadlockAdvisor.Tests;

/// <summary>One copy of the app per data folder: the second start signals the first and leaves.</summary>
public class SingleInstanceTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(5);

    private static string UniqueName() => "DeadlockAdvisor-Test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void OnlyTheFirstCopyBecomesPrimary()
    {
        var name = UniqueName();

        using var first = SingleInstance.TryBecomePrimary(name);
        using var second = SingleInstance.TryBecomePrimary(name);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void ASignalReachesThePrimaryOnce()
    {
        var name = UniqueName();
        using var primary = SingleInstance.TryBecomePrimary(name)!;
        using var signalled = new SemaphoreSlim(0);
        primary.Listen(() => signalled.Release());

        Assert.True(SingleInstance.SignalPrimary(name));

        Assert.True(signalled.Wait(_wait));
        Assert.False(signalled.Wait(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void ASignalSentBeforeTheWindowIsListeningIsNotLost()
    {
        var name = UniqueName();
        using var primary = SingleInstance.TryBecomePrimary(name)!;
        SingleInstance.SignalPrimary(name);
        using var signalled = new SemaphoreSlim(0);

        primary.Listen(() => signalled.Release());

        Assert.True(signalled.Wait(_wait));
    }

    [Fact]
    public void SignallingWithNoPrimaryDoesNothing() =>
        Assert.False(SingleInstance.SignalPrimary(UniqueName()));

    [Fact]
    public void ADisposedPrimaryLetsTheNextCopyTakeOver()
    {
        var name = UniqueName();
        var first = SingleInstance.TryBecomePrimary(name)!;
        first.Listen(() => { });
        first.Dispose();

        using var next = SingleInstance.TryBecomePrimary(name);

        Assert.NotNull(next);
    }

    [Fact]
    public void ADifferentDataFolderGetsItsOwnName()
    {
        var real = SingleInstance.NameFor(Path.Combine(Path.GetTempPath(), "real"));
        var scratch = SingleInstance.NameFor(Path.Combine(Path.GetTempPath(), "scratch"));

        Assert.NotEqual(real, scratch);
        Assert.Equal(real, SingleInstance.NameFor(Path.Combine(Path.GetTempPath(), "real") + Path.DirectorySeparatorChar));
        using var first = SingleInstance.TryBecomePrimary(real);
        using var beside = SingleInstance.TryBecomePrimary(scratch);
        Assert.NotNull(beside);
    }
}
