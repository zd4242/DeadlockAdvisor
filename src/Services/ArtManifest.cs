using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace DeadlockAdvisor.Services;

/// <summary>
/// What the art download fetched, in assets/art_manifest.json: each file's URL, the server's tag for
/// that version of it, and a hash of its bytes. With it a later download only asks whether a file
/// changed (a "not modified" costs nothing), and a file the download never fetched, such as art put
/// there by hand, can be told apart.
/// </summary>
public sealed class ArtManifest
{
    public const string FileName = "art_manifest.json";

    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true };

    private readonly string _assetsDir;
    private readonly Dictionary<string, Entry> _entries;
    private readonly Lock _lock = new();

    /// <param name="ETag">The server's tag for the version downloaded, if it gave one.</param>
    public sealed record Entry(string Url, string? ETag, string Sha256);

    private ArtManifest(string assetsDir, Dictionary<string, Entry> entries)
    {
        _assetsDir = assetsDir;
        _entries = entries;
    }

    /// <summary>The manifest in <paramref name="assetsDir"/>, or an empty one if there's none or it can't be read.</summary>
    public static ArtManifest Load(string assetsDir)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(assetsDir, FileName))) is JsonObject files)
            {
                foreach (var (path, node) in files)
                {
                    if ((string?)node?["url"] is { } url && (string?)node["sha256"] is { } hash)
                        entries[path] = new Entry(url, (string?)node["etag"], hash);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing or unreadable: every file is treated as never downloaded, which only costs a re-check.
        }
        return new ArtManifest(assetsDir, entries);
    }

    public Entry? Get(string path)
    {
        lock (_lock)
            return _entries.GetValueOrDefault(Key(path));
    }

    public void Set(string path, Entry entry)
    {
        lock (_lock)
            _entries[Key(path)] = entry;
    }

    public void Remove(string path)
    {
        lock (_lock)
            _entries.Remove(Key(path));
    }

    public void Save()
    {
        KeyValuePair<string, Entry>[] entries;
        lock (_lock)
            entries = [.. _entries];
        var json = new JsonObject();
        foreach (var (path, entry) in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            json[path] = new JsonObject { ["url"] = entry.Url, ["etag"] = entry.ETag, ["sha256"] = entry.Sha256 };
        Directory.CreateDirectory(_assetsDir);
        AtomicFile.Write(Path.Combine(_assetsDir, FileName), System.Text.Encoding.UTF8.GetBytes(json.ToJsonString(_indented) + "\n"));
    }

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>A file's key: its path under the assets folder, with forward slashes.</summary>
    private string Key(string path) => Path.GetRelativePath(_assetsDir, path).Replace('\\', '/');
}
