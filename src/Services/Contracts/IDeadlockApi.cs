using System.Text.Json.Nodes;
using System.Threading;

namespace DeadlockAdvisor.Services.Contracts;

/// <summary>
/// HTTP to the community API at deadlock-api.com. Failures throw: <see cref="System.Net.Http.HttpRequestException"/>
/// (with a status code for an HTTP error), <see cref="TimeoutException"/>, or a JSON parse error.
/// </summary>
public interface IDeadlockApi
{
    Task<JsonNode?> GetJsonAsync(string url, CancellationToken cancellationToken = default);

    Task<byte[]> GetBytesAsync(string url, string userAgent, CancellationToken cancellationToken = default);
}
