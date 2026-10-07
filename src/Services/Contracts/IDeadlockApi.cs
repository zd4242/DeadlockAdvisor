using System.IO;
using System.Reactive.Linq;
using System.Text.Json.Nodes;
using System.Threading;

namespace DeadlockAdvisor.Services.Contracts;

/// <summary>
/// HTTP to the community API at deadlock-api.com, and to GitHub for the shared match data. Failures throw:
/// <see cref="System.Net.Http.HttpRequestException"/> (with a status code for an HTTP error), <see cref="TimeoutException"/>,
/// or a JSON parse error.
/// </summary>
public interface IDeadlockApi
{
    /// <summary>Every answer's size as it came over the wire, compressed or not, since this was made.</summary>
    long BytesReceived { get; }

    /// <summary>
    /// Each request's outcome, by host: reached when the server answered at all, even with an error status, and not
    /// reached when the connection failed or timed out. Never emits for an implementation that doesn't track it.
    /// </summary>
    IObservable<HostReach> Reachability => Observable.Never<HostReach>();

    /// <summary>Asks for the answer compressed, which shrinks JSON several times over.</summary>
    Task<JsonNode?> GetJsonAsync(string url, CancellationToken cancellationToken = default);

    Task<byte[]> GetBytesAsync(string url, string userAgent, CancellationToken cancellationToken = default);

    /// <summary>
    /// A file, unless the copy tagged <paramref name="etag"/> is still current, in which case the server
    /// answers "not modified" and <see cref="ChangedFile.Bytes"/> is null.
    /// </summary>
    Task<ChangedFile> GetBytesIfChangedAsync(string url, string userAgent, string? etag, CancellationToken cancellationToken = default);

    /// <summary>
    /// A large file, streamed into <paramref name="destination"/> as it arrives. It times out only when the
    /// server goes quiet, not on a long download.
    /// </summary>
    Task DownloadAsync(string url, string userAgent, Stream destination, IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <param name="Reached">False when the connection failed or timed out; true when the server answered, however it answered.</param>
public readonly record struct HostReach(string Host, bool Reached);

/// <param name="Total">The size the server gave; 0 when it gave none.</param>
public readonly record struct DownloadProgress(long Done, long Total);

/// <param name="Bytes">The file, or null when the copy asked about is still current.</param>
/// <param name="ETag">The server's tag for this version of the file, to ask about next time.</param>
public sealed record ChangedFile(byte[]? Bytes, string? ETag);
