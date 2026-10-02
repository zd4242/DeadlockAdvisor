using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DeadlockAdvisor.Scoring;

namespace DeadlockAdvisor.Services;

/// <summary>One patch's counts in the shared snapshot: a gzipped <see cref="MatchSegment"/>, and what it covers.</summary>
/// <param name="File">"2026-09-29-1a2b3c4d.json.gz": named by its contents, so a file the manifest names never changes under it.</param>
/// <param name="Bytes">The gzipped file's size.</param>
/// <param name="Sha256">The gzipped file's hash, lowercase hex.</param>
public sealed record SnapshotPatch(string File, string Title, long Start, long Until, bool Ended, long FetchedAt, bool Ranks, long Bytes, string Sha256)
{
    public Patch Patch => new(Title, Start);

    public bool Complete => Ended && FetchedAt >= Until + MatchSegment.SettleSeconds;
}

/// <summary>
/// The match counts a scheduled GitHub Actions job downloads from deadlock-api.com and publishes as
/// assets of the repo's rolling "match-data" release (tools/MatchSnapshot,
/// .github/workflows/match-data.yml), so the app downloads a few hundred KB in seconds instead of
/// making hundreds of calls each. Every patch comes with its rank groups. The manifest lists the
/// patches; each is gzipped JSON exactly as the app stores it, checked against its size and hash.
/// </summary>
/// <param name="CheckedAt">Unix seconds: when the job last brought the snapshot up to date, even if nothing had changed.</param>
/// <param name="Patches">Newest first.</param>
public sealed record MatchSnapshot(long CheckedAt, IReadOnlyList<SnapshotPatch> Patches)
{
    public const int Version = 1;
    public const string ManifestFile = "manifest.json";
    public const string Source = "https://api.deadlock-api.com";
    public const string ReleaseUrl = "https://github.com/zd4242/DeadlockAdvisor/releases/download/match-data";
    public const string ManifestUrl = $"{ReleaseUrl}/{ManifestFile}";

    /// <summary>A snapshot the job hasn't brought up to date for this long is taken to have stopped: deadlock-api.com is asked instead.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(2);

    /// <summary>
    /// How often the job fetches the current patch again, and how old the app's copy gets before an
    /// update fetches the snapshot's newer one. A new or ended patch is fetched at the job's next run.
    /// </summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(12);

    public static string UrlOf(SnapshotPatch patch) => $"{ReleaseUrl}/{patch.File}";

    public bool IsStale(long now) => now - CheckedAt >= StaleAfter.TotalSeconds;

    public byte[] ToJsonBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteString("source", Source);
            writer.WriteNumber("checked_at", CheckedAt);
            writer.WriteStartArray("patches");
            foreach (var patch in Patches)
            {
                writer.WriteStartObject();
                writer.WriteString("file", patch.File);
                writer.WriteString("title", patch.Title);
                writer.WriteNumber("start", patch.Start);
                writer.WriteNumber("until", patch.Until);
                writer.WriteBoolean("ended", patch.Ended);
                writer.WriteNumber("fetched_at", patch.FetchedAt);
                writer.WriteBoolean("ranks", patch.Ranks);
                writer.WriteNumber("bytes", patch.Bytes);
                writer.WriteString("sha256", patch.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Throws <see cref="FormatException"/> for another version, or a JSON / missing-property error on a malformed manifest.</summary>
    public static MatchSnapshot Parse(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != Version)
            throw new FormatException($"Not a version {Version} match snapshot.");
        var patches = root.GetProperty("patches").EnumerateArray()
            .Select(patch => new SnapshotPatch(
                patch.GetProperty("file").GetString() ?? "",
                patch.GetProperty("title").GetString() ?? "",
                patch.GetProperty("start").GetInt64(),
                patch.GetProperty("until").GetInt64(),
                patch.GetProperty("ended").GetBoolean(),
                patch.GetProperty("fetched_at").GetInt64(),
                patch.GetProperty("ranks").GetBoolean(),
                patch.GetProperty("bytes").GetInt64(),
                patch.GetProperty("sha256").GetString() ?? ""))
            .OrderByDescending(patch => patch.Start)
            .ToList();
        if (patches.Any(patch => patch.File.Length == 0 || patch.File.Contains('/') || patch.File.Contains('\\')))
            throw new FormatException("A match snapshot entry has no usable file name.");
        return new MatchSnapshot(root.GetProperty("checked_at").GetInt64(), patches);
    }

    /// <summary>A segment as the snapshot publishes it: its entry, and the gzipped file the entry describes.</summary>
    public static (SnapshotPatch Entry, byte[] Gzipped) Pack(MatchSegment segment)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(segment.ToJsonBytes());
        var gzipped = output.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(gzipped));
        var entry = new SnapshotPatch($"{segment.Patch.Date}-{hash[..8]}.json.gz", segment.Patch.Title, segment.Patch.Start,
            segment.Until, segment.Ended, segment.FetchedAt, segment.HasRanks, gzipped.Length, hash);
        return (entry, gzipped);
    }

    /// <summary>What reading a missing, damaged or foreign manifest or file throws.</summary>
    public static bool IsUnusable(Exception ex) =>
        ex is IOException or InvalidDataException or FormatException or JsonException or KeyNotFoundException or InvalidOperationException;

    /// <summary>The segment in a downloaded file. Throws <see cref="InvalidDataException"/> when the file isn't the one the entry describes.</summary>
    public static MatchSegment Unpack(SnapshotPatch entry, byte[] gzipped)
    {
        if (gzipped.Length != entry.Bytes || Convert.ToHexStringLower(SHA256.HashData(gzipped)) != entry.Sha256)
            throw new InvalidDataException($"The shared match data for patch {entry.Patch.Label} didn't arrive intact.");
        using var input = new GZipStream(new MemoryStream(gzipped), CompressionMode.Decompress);
        using var json = new MemoryStream();
        input.CopyTo(json);
        var segment = MatchSegment.Parse(json.ToArray());
        if (segment.Patch.Start != entry.Start || segment.FetchedAt != entry.FetchedAt)
            throw new InvalidDataException($"The shared match data for patch {entry.Patch.Label} holds another patch's counts.");
        return segment;
    }
}
