using System.Text.Json.Nodes;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Tests.Support;

/// <summary>
/// Reference outputs from the Python app (its scripts/export_golden.py), copied next to the tests.
/// Regenerate with: python scripts/export_golden.py &lt;this repo&gt;/tests/DeadlockAdvisor.Tests/Golden
/// </summary>
public static class Golden
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Golden");

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
    /// <summary>Equal to about nine significant digits: float sums can come out in a different order than Python's.</summary>
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
