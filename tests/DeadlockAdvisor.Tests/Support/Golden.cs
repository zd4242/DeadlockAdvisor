using System.Text;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Formats;

namespace DeadlockAdvisor.Tests.Support;

/// <summary>
/// Reference outputs, copied next to the tests. A deliberate behaviour change regenerates the affected
/// ones: run the tests with DEADLOCK_UPDATE_GOLDENS=1, review the diff under
/// tests/DeadlockAdvisor.Tests/Golden, and commit.
/// </summary>
public static class Golden
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Golden");

    /// <summary>Tests that support it rewrite their golden files from the current output instead of comparing.</summary>
    public static bool Updating => Environment.GetEnvironmentVariable("DEADLOCK_UPDATE_GOLDENS") == "1";

    /// <summary>A golden file in the source tree: <see cref="Root"/> is only the build's copy.</summary>
    public static string SourcePathOf(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeadlockAdvisor.Tests.csproj")))
            dir = dir.Parent;
        if (dir is null)
            throw new DirectoryNotFoundException("Couldn't find the test project above " + AppContext.BaseDirectory);
        return Path.Combine([dir.FullName, "Golden", .. parts]);
    }

    /// <summary>
    /// Rewrite a golden JSON file, in the source tree and the build's copy, in the data files' JSON
    /// layout (<see cref="DataJson"/>), keeping the file's line endings so the diff shows only real changes.
    /// </summary>
    public static void WriteJson(string name, JsonNode node)
    {
        var source = SourcePathOf(name);
        var text = Encoding.UTF8.GetString(DataJson.ToFileBytes(node, ensureAscii: false));
        if (File.Exists(source) && !File.ReadAllText(source).Contains("\r\n", StringComparison.Ordinal))
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        File.WriteAllBytes(source, bytes);
        File.WriteAllBytes(PathOf(name), bytes);
    }

    /// <summary>Replace a golden file with one a test produced, in the source tree and the build's copy.</summary>
    public static void CopyFile(string actualPath, params string[] parts)
    {
        File.Copy(actualPath, SourcePathOf(parts), overwrite: true);
        File.Copy(actualPath, PathOf(parts), overwrite: true);
    }

    public static string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);

    public static string DataDir => PathOf("data");

    public static JsonNode Json(string name) => JsonNode.Parse(File.ReadAllText(PathOf(name)))!;

    public static DataStore LoadStore() => DataStore.Load(DataDir);

    /// <summary>A throwaway copy of the snapshot data, for tests that save.</summary>
    public static TempDirectory CopyData()
    {
        var temp = new TempDirectory();
        foreach (var file in Directory.GetFiles(DataDir))
            File.Copy(file, Path.Combine(temp.Path, Path.GetFileName(file)));
        return temp;
    }

    public static double Number(JsonNode? node) => node!.GetValue<double>();

    public static double? NullableNumber(JsonNode? node) => node is null ? null : node.GetValue<double>();

    public static string Text(JsonNode? node) => node!.GetValue<string>();

    public static IEnumerable<JsonNode> Items(JsonNode? node) => node!.AsArray().Select(item => item!);
}

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DeadlockAdvisorTests", Guid.NewGuid().ToString("N"));

    public TempDirectory()
    {
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}

public static class AssertEx
{
    /// <summary>Equal to about nine significant digits: float sums in a different order can differ in the last bits.</summary>
    public static void Close(double expected, double actual, double relative = 1e-9, string? because = null)
    {
        var tolerance = relative * Math.Max(1.0, Math.Max(Math.Abs(expected), Math.Abs(actual)));
        if (Math.Abs(expected - actual) > tolerance)
            Assert.Fail($"Expected {expected:R} but got {actual:R}{(because is null ? "" : $" ({because})")}");
    }

    public static void Close(double? expected, double? actual, double relative = 1e-9, string? because = null)
    {
        if (expected is null || actual is null)
        {
            Assert.True(expected is null && actual is null, $"Expected {expected} but got {actual}{(because is null ? "" : $" ({because})")}");
            return;
        }
        Close(expected.Value, actual.Value, relative, because);
    }

    public static void BytesEqual(string expectedPath, string actualPath)
    {
        var expected = File.ReadAllBytes(expectedPath);
        var actual = File.ReadAllBytes(actualPath);
        if (expected.AsSpan().SequenceEqual(actual))
            return;

        var index = 0;
        while (index < expected.Length && index < actual.Length && expected[index] == actual[index])
            index++;
        var line = 1 + expected.AsSpan(0, index).Count((byte)'\n');
        string Around(byte[] bytes) =>
            System.Text.Encoding.UTF8.GetString(bytes, Math.Max(0, index - 60), Math.Min(120, bytes.Length - Math.Max(0, index - 60)));
        Assert.Fail($"{Path.GetFileName(expectedPath)} differs at byte {index} (line {line}), lengths {expected.Length} vs {actual.Length}.\n"
                    + $"expected: …{Around(expected)}…\nactual:   …{Around(actual)}…");
    }
}
