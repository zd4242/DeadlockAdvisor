using System.Text.Json.Nodes;
using System.Threading;

namespace DeadlockAdvisor.Services.Contracts;

/// <summary>
/// HTTP to the community API at deadlock-api.com. Failures throw: <see cref="System.Net.Http.HttpRequestException"/>
/// (with a status code for an HTTP error), <see cref="TimeoutException"/>, or a JSON parse error.
/// </summary>
public interface IDeadlockApi
{
    /// <summary>Every answer's size as it came over the wire, compressed or not, since this was made.</summary>
    long BytesReceived { get; }

    /// <summary>Asks for the answer compressed, which shrinks JSON several times over.</summary>
    Task<JsonNode?> GetJsonAsync(string url, CancellationToken cancellationToken = default);

    Task<byte[]> GetBytesAsync(string url, string userAgent, CancellationToken cancellationToken = default);

    /// <summary>
    /// A file, unless the copy tagged <paramref name="etag"/> is still current, in which case the server
    /// answers "not modified" and <see cref="ChangedFile.Bytes"/> is null.
    /// </summary>
    Task<ChangedFile> GetBytesIfChangedAsync(string url, string userAgent, string? etag, CancellationToken cancellationToken = default);
}

/// <param name="Bytes">The file, or null when the copy asked about is still current.</param>
/// <param name="ETag">The server's tag for this version of the file, to ask about next time.</param>
public sealed record ChangedFile(byte[]? Bytes, string? ETag);
