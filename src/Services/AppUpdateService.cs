using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

/// <summary>A release of the app on GitHub.</summary>
/// <param name="Version">"0.2.0": its tag without the "v".</param>
/// <param name="Url">Its page, with what's new and the download.</param>
/// <param name="Exe">The Windows download, when the release has one GitHub gives a hash for.</param>
public sealed record AppRelease(string Version, string Url, AppDownload? Exe = null);

/// <param name="Sha256">Lowercase hex, as GitHub gives it for each release file.</param>
public sealed record AppDownload(string Url, long Size, string Sha256);

public interface IAppUpdateService
{
    /// <summary>The version running, to compare with; null for a build made outside the release workflow.</summary>
    Version? Current { get; }

    /// <summary>The newest release; null when GitHub can't be reached or answers with something else.</summary>
    Task<AppRelease?> LatestAsync(CancellationToken cancellationToken = default);

    /// <summary>The release's exe can take the running one's place: a release build, running as its own exe on Windows.</summary>
    bool CanInstall(AppRelease release);

    /// <summary>
    /// Download the release's exe beside the running one and check it against its size and hash, then swap it
    /// in: the running exe is renamed out of the way, which Windows allows, and the new one takes its name, so
    /// the next start runs it. Throws an HTTP / timeout error, <see cref="InvalidDataException"/> for a file
    /// that didn't arrive intact, or an <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>
    /// when the exe's folder can't be written to.
    /// </summary>
    Task InstallAsync(AppRelease release, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken = default);

    /// <summary>Start the installed exe once this one has shut down (<see cref="AppUpdateService.RestartIfAsked"/>).</summary>
    void RestartAfterExit(bool restart = true);

    /// <summary>Delete what the last update left behind: the exe it replaced, once that has exited.</summary>
    Task CleanUpAsync();
}

/// <summary>
/// The newest release, from GitHub's API: one small request, which leaves out the match-data and model
/// pre-releases. On Windows, a release build can install the newer exe in place of itself.
/// </summary>
public sealed class AppUpdateService : IAppUpdateService
{
    public const string LatestUrl = "https://api.github.com/repos/zd4242/DeadlockAdvisor/releases/latest";

    /// <summary>The Windows download's name in every release.</summary>
    public const string ExeName = "DeadlockAdvisor.exe";

    private static string? _restartExe;

    private readonly IDeadlockApi _api;
    private readonly string? _exe;

    public AppUpdateService(IDeadlockApi api)
        : this(api, AppVersion.Release, RunningExe())
    {
    }

    /// <param name="exe">The exe that's running, to replace; null when this isn't one that can be.</param>
    internal AppUpdateService(IDeadlockApi api, Version? current, string? exe = null)
    {
        _api = api;
        Current = current;
        _exe = exe;
    }

    public Version? Current { get; }

    /// <summary>The exe being replaced, renamed out of the way until it has exited.</summary>
    public static string OldPath(string exe) => exe + ".old";

    /// <summary>The download, until it's checked and swapped in.</summary>
    public static string NewPath(string exe) => exe + ".new";

    public async Task<AppRelease?> LatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var json = JsonDocument.Parse(await _api.GetBytesAsync(LatestUrl, DeadlockApi.UserAgent, cancellationToken));
            var root = json.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            var url = root.GetProperty("html_url").GetString();
            return string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(url) ? null : new AppRelease(tag.TrimStart('v'), url, ExeOf(root));
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static AppDownload? ExeOf(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets))
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != ExeName)
                continue;
            var digest = asset.TryGetProperty("digest", out var given) ? given.GetString() : null;
            return digest is not null && digest.StartsWith("sha256:", StringComparison.Ordinal)
                ? new AppDownload(asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64(),
                    digest["sha256:".Length..].ToLowerInvariant())
                : null;
        }
        return null;
    }

    public bool CanInstall(AppRelease release) => Current is not null && _exe is not null && release.Exe is not null;

    public async Task InstallAsync(AppRelease release, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (!CanInstall(release))
            throw new InvalidOperationException("This copy of the app can't install a release in its place.");
        var exe = _exe!;
        var download = release.Exe!;
        var downloaded = NewPath(exe);
        try
        {
            await using (var file = new FileStream(downloaded, FileMode.Create, FileAccess.Write, FileShare.None))
                await _api.DownloadAsync(download.Url, DeadlockApi.UserAgent, file, progress, cancellationToken);
            if (new FileInfo(downloaded).Length != download.Size || !Convert.ToHexStringLower(await HashAsync(downloaded, cancellationToken)).Equals(download.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"The download of version {release.Version} didn't arrive intact.");
        }
        catch
        {
            TryDelete(downloaded);
            throw;
        }
        Swap(exe, downloaded);
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        return await SHA256.HashDataAsync(file, cancellationToken);
    }

    /// <summary>Put <paramref name="downloaded"/> in place of <paramref name="exe"/>, which is kept beside it until it has exited.</summary>
    internal static void Swap(string exe, string downloaded)
    {
        var old = OldPath(exe);
        File.Move(exe, old, overwrite: true);
        try
        {
            File.Move(downloaded, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
    }

    public void RestartAfterExit(bool restart = true) => _restartExe = restart ? _exe : null;

    /// <summary>Called once the app has shut down: start the installed exe, if a restart was asked for.</summary>
    public static void RestartIfAsked()
    {
        if (_restartExe is { } exe && File.Exists(exe))
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
    }

    public async Task CleanUpAsync()
    {
        if (_exe is null)
            return;
        TryDelete(NewPath(_exe));
        // The exe replaced may take a moment to exit after starting this one.
        var old = OldPath(_exe);
        for (var attempt = 0; attempt < 10 && File.Exists(old) && !TryDelete(old); attempt++)
            await Task.Delay(TimeSpan.FromSeconds(2));
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The exe running, when it's a release's single-file build on Windows, under whatever name it was saved as,
    /// rather than a build run through dotnet.
    /// </summary>
    private static string? RunningExe() =>
        OperatingSystem.IsWindows() && AppVersion.Release is not null && Environment.ProcessPath is { } path
        && !Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
}
