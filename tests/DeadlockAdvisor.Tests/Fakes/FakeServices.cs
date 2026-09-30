using System.Net.Http;
using System.Reactive.Subjects;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Tests.Fakes;

public sealed class FakeSettingsService : ISettingsService
{
    private readonly BehaviorSubject<AppSettings> _settings = new(new AppSettings());

    public AppSettings Current => _settings.Value;
    public IObservable<AppSettings> SettingsChanged => _settings;

    public void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        _settings.OnNext(Current);
    }

    public Task LoadAsync() => Task.CompletedTask;
}

/// <summary>deadlock-api.com with canned answers per URL; anything else fails as if the site were down.</summary>
public sealed class FakeDeadlockApi : IDeadlockApi
{
    public Dictionary<string, Func<JsonNode?>> Json { get; } = [];
    public Dictionary<string, byte[]> Bytes { get; } = [];
    public List<string> Asked { get; } = [];

    /// <summary>The URLs a "not modified" came back for.</summary>
    public List<string> NotModified { get; } = [];

    /// <summary>A file's tag is its contents, so changing the bytes changes it, as a real server's would.</summary>
    public static string ETagOf(byte[] bytes) => $"\"{Convert.ToHexString(bytes)}\"";

    public async Task<ChangedFile> GetBytesIfChangedAsync(string url, string userAgent, string? etag, CancellationToken cancellationToken = default)
    {
        var bytes = await GetBytesAsync(url, userAgent, cancellationToken);
        if (etag == ETagOf(bytes))
        {
            NotModified.Add(url);
            return new ChangedFile(null, etag);
        }
        return new ChangedFile(bytes, ETagOf(bytes));
    }

    public Task<JsonNode?> GetJsonAsync(string url, CancellationToken cancellationToken = default)
    {
        Asked.Add(url);
        return Json.TryGetValue(url, out var answer)
            ? Task.FromResult(answer())
            : throw new HttpRequestException($"offline (test): {url}");
    }

    public Task<byte[]> GetBytesAsync(string url, string userAgent, CancellationToken cancellationToken = default)
    {
        Asked.Add(url);
        return Bytes.TryGetValue(url, out var bytes)
            ? Task.FromResult(bytes)
            : throw new HttpRequestException($"offline (test): {url}");
    }
}

public sealed class FakeLoggingService : ILoggingService
{
    public void Log(LogLevel level, string message) { }
    public void Log(LogLevel level, string message, Exception exception) { }
    public void Debug(string message) { }
    public void Information(string message) { }
    public void Warning(string message) { }
    public void Error(string message) { }
    public void Error(string message, Exception exception) { }
}
