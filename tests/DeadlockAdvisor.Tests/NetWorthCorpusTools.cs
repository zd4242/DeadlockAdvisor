using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Tests.Ui;
using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Tests;

/// <summary>Looking at what the net worth reader makes of kept captures. Not a test: it does nothing unless its variable is set.</summary>
public class NetWorthCorpusTools
{
    /// <summary>
    /// DEADLOCK_NETWORTH_DUMP=&lt;a folder of kept captures (scoreboard_*.png or detect_*.png beside its .json)&gt;: for each, every pill's and total's
    /// glyphs with their best digit, score and margin, and what the reader says, into mockups/net_worth_dump.txt.
    /// </summary>
    [Fact]
    public void DumpWhatTheReaderSees()
    {
        if (Environment.GetEnvironmentVariable("DEADLOCK_NETWORTH_DUMP") is not { Length: > 0 } folder)
            return;
        var report = new StringBuilder();
        var glyphs = NetWorthGlyphs.Bundled;
        foreach (var path in Directory.GetFiles(folder, "*.png").Where(path => Path.GetFileName(path).StartsWith("scoreboard_") || Path.GetFileName(path).StartsWith("detect_")).Order(StringComparer.Ordinal))
        {
            var note = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")))!;
            var grid = Geometry.Parse(note["grid"]!.AsObject())!;
            var image = ImageFile.Load(path);
            var reading = NetWorthReader.Read(image, grid, glyphs);
            var heroes = note["heroes"] as JsonArray;
            report.AppendLine($"== {Path.GetFileName(path)} {image.Width}x{image.Height}");
            report.AppendLine($"pills  {string.Join(" ", reading.Pills.Select(souls => souls?.ToString(CultureInfo.InvariantCulture) ?? "?"))}");
            report.AppendLine($"totals {string.Join(" ", reading.Totals.Select(souls => souls?.ToString(CultureInfo.InvariantCulture) ?? "?"))}  agree {reading.Agrees(0)}/{reading.Agrees(1)}  read {reading.ReadCount}");
            var frames = NetWorthReader.DigitFrames(image, grid);
            for (var i = 0; i < frames.Count; i++)
            {
                var what = i < Layout.SlotCount ? $"slot {i} {(string?)heroes?[i]}" : $"total {i - Layout.SlotCount}";
                var read = frames[i].Select(frame => glyphs.Classify(frame)).Select(match => string.Create(CultureInfo.InvariantCulture, $"{match.Digit}({match.Score:0.00}/{match.Margin:0.00})"));
                report.AppendLine($"  {what}: {string.Join(" ", read)}");
            }
        }
        File.WriteAllText(UiHarness.MockupPath("net_worth_dump.txt"), report.ToString());
    }
}
