namespace DeadlockAdvisor.Services;

/// <summary>A data file that exists but can't be used: a row that doesn't parse, or a base table with no rows.</summary>
public sealed class DataLoadException(string file, string reason, Exception? inner = null)
    : Exception($"{file}: {reason}", inner)
{
    /// <summary>The file's name inside the data folder.</summary>
    public string File { get; } = file;

    public string Reason { get; } = reason;
}
