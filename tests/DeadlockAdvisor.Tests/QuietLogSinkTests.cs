using Avalonia.Logging;
using DeadlockAdvisor.Core;

namespace DeadlockAdvisor.Tests;

public class QuietLogSinkTests
{
    private sealed class RecordingSink : ILogSink
    {
        public List<string> Messages { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area) => true;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Messages.Add($"{area}: {messageTemplate}");

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Messages.Add($"{area}: {messageTemplate}");
    }

    [Fact]
    public void OnlyTheClosedWindowInputWarningIsDropped()
    {
        var inner = new RecordingSink();
        var sink = new QuietLogSink(inner);

        sink.Log(LogEventLevel.Warning, LogArea.Control, null, QuietLogSink.ClosedWindowInput);
        sink.Log(LogEventLevel.Warning, LogArea.Binding, null, "Could not find a matching property accessor for {Property}", "Nope");
        sink.Log(LogEventLevel.Warning, LogArea.Control, null, "Some other control warning");

        Assert.Equal(["Binding: Could not find a matching property accessor for {Property}", "Control: Some other control warning"], inner.Messages);
    }
}
