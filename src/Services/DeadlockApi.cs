using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

/// <summary>One shared <see cref="HttpClient"/> for every call to deadlock-api.com, identified as the Python app identifies itself.</summary>
public sealed class DeadlockApi : IDeadlockApi, IDisposable
{
    public const string UserAgent = "deadlock-advisor/1.0";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;

    public DeadlockApi()
        : this(new HttpClientHandler())
    {
    }

    internal DeadlockApi(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = Timeout };
    }

    public async Task<JsonNode?> GetJsonAsync(string url, CancellationToken cancellationToken = default)
    {
        var bytes = await GetBytesAsync(url, UserAgent, cancellationToken);
        return JsonNode.Parse(bytes);
    }

    public async Task<byte[]> GetBytesAsync(string url, string userAgent, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{new Uri(url).Host} didn't answer within {Timeout.TotalSeconds:0} seconds.", ex);
        }
    }

    public void Dispose() => _http.Dispose();
}
