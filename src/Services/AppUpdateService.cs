using System.Net.Http;
using System.Text.Json;
using System.Threading;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

/// <summary>A release of the app on GitHub.</summary>
/// <param name="Version">"0.2.0": its tag without the "v".</param>
/// <param name="Url">Its page, with what's new and the download.</param>
public sealed record AppRelease(string Version, string Url);

public interface IAppUpdateService
{
    /// <summary>The version running, to compare with; null for a build made outside the release workflow.</summary>
    Version? Current { get; }

    /// <summary>The newest release; null when GitHub can't be reached or answers with something else.</summary>
    Task<AppRelease?> LatestAsync(CancellationToken cancellationToken = default);
}

/// <summary>The newest release, from GitHub's API: one small request, which leaves out the match-data and model pre-releases.</summary>
public sealed class AppUpdateService : IAppUpdateService
{
    public const string LatestUrl = "https://api.github.com/repos/zd4242/DeadlockAdvisor/releases/latest";

    private readonly IDeadlockApi _api;

    public AppUpdateService(IDeadlockApi api)
        : this(api, AppVersion.Release)
    {
    }

    internal AppUpdateService(IDeadlockApi api, Version? current)
    {
        _api = api;
        Current = current;
    }

    public Version? Current { get; }

    public async Task<AppRelease?> LatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var json = JsonDocument.Parse(await _api.GetBytesAsync(LatestUrl, DeadlockApi.UserAgent, cancellationToken));
            var root = json.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            var url = root.GetProperty("html_url").GetString();
            return string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(url) ? null : new AppRelease(tag.TrimStart('v'), url);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
