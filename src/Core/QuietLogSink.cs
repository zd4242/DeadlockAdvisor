using Avalonia.Logging;

namespace DeadlockAdvisor.Core;

/// <summary>
/// Avalonia's log, minus the warning it raises whenever the tail of a click (the release, the pointer
/// leaving) reaches a menu popup or dialog that the click itself just closed. That fires on nearly
/// every menu pick and modal button, and means nothing.
/// </summary>
public sealed class QuietLogSink(ILogSink inner) : ILogSink
{
    public const string ClosedWindowInput = "PlatformImpl is null, couldn't handle input.";

    public bool IsEnabled(LogEventLevel level, string area) => inner.IsEnabled(level, area);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (!IsNoise(area, messageTemplate))
            inner.Log(level, area, source, messageTemplate);
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (!IsNoise(area, messageTemplate))
            inner.Log(level, area, source, messageTemplate, propertyValues);
    }

    private static bool IsNoise(string area, string messageTemplate) =>
        area == LogArea.Control && messageTemplate == ClosedWindowInput;
}

public static class QuietLogSinkExtensions
{
    /// <summary><c>LogToTrace()</c>, without the closed-window input warnings.</summary>
    public static AppBuilder LogToTraceQuietly(this AppBuilder builder, LogEventLevel level = LogEventLevel.Warning)
    {
        builder.LogToTrace(level);
        if (Logger.Sink is { } trace)
            Logger.Sink = new QuietLogSink(trace);
        return builder;
    }
}
