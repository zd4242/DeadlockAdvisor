using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

/// <summary>One shared <see cref="HttpClient"/> for every call to deadlock-api.com, identified by <see cref="UserAgent"/>.</summary>
public sealed class DeadlockApi : IDeadlockApi, IDisposable
{
    public const string UserAgent = "deadlock-advisor/1.0";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private long _bytesReceived;

    public DeadlockApi()
        : this(new HttpClientHandler())
    {
    }

    internal DeadlockApi(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = Timeout };
    }

    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    public async Task<JsonNode?> GetJsonAsync(string url, CancellationToken cancellationToken = default)
    {
        var bytes = (await SendAsync(url, UserAgent, null, compressed: true, cancellationToken)).Bytes!;
        return JsonNode.Parse(bytes);
    }

    public async Task<byte[]> GetBytesAsync(string url, string userAgent, CancellationToken cancellationToken = default) =>
        (await GetBytesIfChangedAsync(url, userAgent, null, cancellationToken)).Bytes!;

    // Art goes uncompressed: images barely shrink, and a compressed copy can carry a different tag.
    public Task<ChangedFile> GetBytesIfChangedAsync(string url, string userAgent, string? etag, CancellationToken cancellationToken = default) =>
        SendAsync(url, userAgent, etag, compressed: false, cancellationToken);

    private async Task<ChangedFile> SendAsync(string url, string userAgent, string? etag, bool compressed, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        if (etag is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        if (compressed)
        {
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        }
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (etag is not null && response.StatusCode == System.Net.HttpStatusCode.NotModified)
                return new ChangedFile(null, etag);
            response.EnsureSuccessStatusCode();
            var wire = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            Interlocked.Add(ref _bytesReceived, wire.Length);
            return new ChangedFile(Decode(wire, response.Content.Headers.ContentEncoding), response.Headers.ETag?.ToString());
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{new Uri(url).Host} didn't answer within {Timeout.TotalSeconds:0} seconds.", ex);
        }
    }

    public async Task DownloadAsync(string url, string userAgent, Stream destination, IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        // The client's timeout covers the answer's headers; after that, each read gets as long again.
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[81920];
            long done = 0;
            while (true)
            {
                quiet.CancelAfter(Timeout);
                var read = await body.ReadAsync(buffer, quiet.Token);
                if (read == 0)
                    break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                Interlocked.Add(ref _bytesReceived, read);
                progress?.Report(new DownloadProgress(done, total));
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{new Uri(url).Host} stopped answering for {Timeout.TotalSeconds:0} seconds.", ex);
        }
    }

    /// <summary>The answer as sent, undone from whichever of the encodings asked for the server used.</summary>
    private static byte[] Decode(byte[] wire, ICollection<string> encodings)
    {
        var encoding = encodings.LastOrDefault()?.ToLowerInvariant();
        if (encoding is not ("gzip" or "br"))
            return wire;
        using var input = new MemoryStream(wire);
        using Stream decoder = encoding == "gzip"
            ? new GZipStream(input, CompressionMode.Decompress)
            : new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
