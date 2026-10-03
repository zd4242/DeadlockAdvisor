using System.Diagnostics;
using System.Globalization;
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
    /// Download the release's exe beside the running one (<see cref="AppUpdateService.UpdatePath"/>) and check it
    /// against its size and hash. It's installed once the app has closed (<see cref="AppUpdateService.InstallIfDownloaded"/>).
    /// Throws an HTTP / timeout error, <see cref="InvalidDataException"/> for a file that didn't arrive intact, or an
    /// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> when the exe's folder can't be written to.
    /// </summary>
    Task DownloadAsync(AppRelease release, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken = default);

    /// <summary>Whether the version downloaded starts once it's installed, as the app closes.</summary>
    void RestartAfterExit(bool restart = true);

    /// <summary>Delete what the last update left behind, once its installer has exited.</summary>
    Task CleanUpAsync();
}

/// <summary>
/// The newest release, from GitHub's API: one small request, which leaves out the match-data and model
/// pre-releases. On Windows, a release build can download the newer exe and install it in place of itself.
/// A single-file app reads its own exe as it goes, so that file mustn't change under it: the download waits
/// beside it until the app closes. Then a copy of this exe runs as the installer (<see cref="InstallerArgument"/>),
/// which waits for this one to exit, copies the download over it, and starts it if asked. The installer is
/// this version, not the new one, so the new one needn't know how.
/// </summary>
public sealed class AppUpdateService : IAppUpdateService
{
    public const string LatestUrl = "https://api.github.com/repos/zd4242/DeadlockAdvisor/releases/latest";

    /// <summary>The Windows download's name in every release.</summary>
    public const string ExeName = "DeadlockAdvisor.exe";

    /// <summary>"--install &lt;download&gt; --over &lt;exe&gt; --after &lt;process id&gt; [--start]": a copy of this exe, run as the installer.</summary>
    public const string InstallerArgument = "--install";

    private static readonly TimeSpan _waitForExit = TimeSpan.FromSeconds(30);

    // Set once a download is checked and ready, for the app's way out (InstallIfDownloaded).
    private static (string Exe, string Update)? _downloaded;
    private static bool _restart;

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

    /// <summary>"DeadlockAdvisor.update.exe": the download, beside the exe it's to replace until the app closes.</summary>
    public static string UpdatePath(string exe) => Path.ChangeExtension(exe, ".update.exe");

    /// <summary>"DeadlockAdvisor.installer.exe": the copy of this exe that installs the download once this one has exited.</summary>
    public static string InstallerPath(string exe) => Path.ChangeExtension(exe, ".installer.exe");

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

    public async Task DownloadAsync(AppRelease release, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (!CanInstall(release))
            throw new InvalidOperationException("This copy of the app can't install a release in its place.");
        var exe = _exe!;
        var download = release.Exe!;
        var update = UpdatePath(exe);
        _downloaded = null;
        try
        {
            await using (var file = new FileStream(update, FileMode.Create, FileAccess.Write, FileShare.None))
                await _api.DownloadAsync(download.Url, DeadlockApi.UserAgent, file, progress, cancellationToken);
            if (new FileInfo(update).Length != download.Size
                || !Convert.ToHexStringLower(await HashAsync(update, cancellationToken)).Equals(download.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"The download of version {release.Version} didn't arrive intact.");
        }
        catch
        {
            TryDelete(update);
            throw;
        }
        _downloaded = (exe, update);
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        return await SHA256.HashDataAsync(file, cancellationToken);
    }

    public void RestartAfterExit(bool restart = true) => _restart = restart;

    /// <summary>
    /// Called once the app has shut down: start a copy of this exe as the installer, which puts the version
    /// downloaded in this one's place once this process has exited, then starts it if a restart was asked for.
    /// </summary>
    public static void InstallIfDownloaded()
    {
        if (_downloaded is not var (exe, update) || !File.Exists(update))
            return;
        var installer = InstallerPath(exe);
        try
        {
            File.Copy(exe, installer, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var start = new ProcessStartInfo(installer) { UseShellExecute = false };
        start.ArgumentList.Add(InstallerArgument);
        start.ArgumentList.Add(update);
        start.ArgumentList.Add("--over");
        start.ArgumentList.Add(exe);
        start.ArgumentList.Add("--after");
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (_restart)
            start.ArgumentList.Add("--start");
        Process.Start(start);
    }

    /// <summary>
    /// The installer's side, when this exe was started with <see cref="InstallerArgument"/>: wait for the app to
    /// exit, copy the download over its exe, and start it if asked. Returns false for an ordinary start.
    /// </summary>
    public static bool InstallIfAsked(string[] args)
    {
        string? Option(string name) => Array.IndexOf(args, name) is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        if (Option(InstallerArgument) is not { } update || Option("--over") is not { } exe)
            return false;
        if (int.TryParse(Option("--after"), CultureInfo.InvariantCulture, out var oldProcess))
        {
            try
            {
                using var old = Process.GetProcessById(oldProcess);
                old.WaitForExit(_waitForExit);
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
        Install(update, exe);
        if (args.Contains("--start") && File.Exists(exe))
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        return true;
    }

    /// <summary>
    /// Copy <paramref name="update"/> over <paramref name="exe"/>, through a temporary copy swapped in whole, so a
    /// failure part-way leaves the old version as it was. Retries for a while, as the old one may still be closing.
    /// </summary>
    internal static bool Install(string update, string exe)
    {
        var copy = exe + ".tmp";
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                File.Copy(update, copy, overwrite: true);
                File.Move(copy, exe, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
        TryDelete(copy);
        return false;
    }

    public async Task CleanUpAsync()
    {
        if (_exe is null)
            return;
        // The installer may take a moment to exit after starting this one.
        foreach (var leftover in new[] { UpdatePath(_exe), InstallerPath(_exe), _exe + ".tmp" })
        {
            for (var attempt = 0; attempt < 10 && File.Exists(leftover) && !TryDelete(leftover); attempt++)
                await Task.Delay(TimeSpan.FromSeconds(2));
        }
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
