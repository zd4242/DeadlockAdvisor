using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

public class LoggingServiceTests
{
    [Fact]
    public void EachRunLogsToAFreshFileAndKeepsThePreviousOne()
    {
        using var folder = new TempDirectory();

        new LoggingService(folder.Path).Warning("first run");
        new LoggingService(folder.Path).Error("second run", new InvalidOperationException("boom"));

        var current = File.ReadAllText(folder.File(LoggingService.FileName));
        Assert.Contains("second run", current);
        Assert.Contains("InvalidOperationException: boom", current);
        Assert.DoesNotContain("first run", current);
        Assert.Contains("first run", File.ReadAllText(folder.File(LoggingService.PreviousFileName)));
    }
}
