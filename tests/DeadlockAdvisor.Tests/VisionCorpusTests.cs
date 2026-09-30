using System.Text.Json.Nodes;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Tests.Ui;
using DeadlockAdvisor.Vision;
using static DeadlockAdvisor.Tests.Support.VisionData;

namespace DeadlockAdvisor.Tests;

/// <summary>
/// Detection against every labelled capture in Golden/vision/corpus: real matches, with faded,
/// critical, on-fire and dead portraits. Golden/vision/corpus_report.json pins which slots are read
/// right; a deliberate change regenerates it (DEADLOCK_UPDATE_GOLDENS=1) and the diff shows what it
/// won and lost. mockups/detection_report.md explains the numbers.
/// </summary>
public class VisionCorpusTests
{
    private static readonly Lazy<List<CaptureOutcome>> _outcomes =
        new(() => VisionEval.Run(CorpusCapture.LoadAll(Golden.PathOf("vision", "corpus")), Bank));

    [Fact]
    public void TheCorpusReadsNoWorseThanItsReport()
    {
        var outcomes = _outcomes.Value;
        Assert.NotEmpty(outcomes);
        var report = Path.Combine(UiHarness.RepoRoot(), "mockups", "detection_report.md");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, VisionEval.Markdown(outcomes, Bank));

        var pinned = VisionEval.Pinned(outcomes);
        if (Golden.Updating)
        {
            Golden.WriteJson("vision/corpus_report.json", pinned);
            return;
        }
        var golden = Golden.Json("vision/corpus_report.json")["captures"]!.AsObject();
        var problems = new List<string>();
        foreach (var (name, node) in pinned["captures"]!.AsObject())
        {
            var now = node!.AsObject();
            var was = golden[name]?.AsObject();
            if (was is null)
            {
                problems.Add($"{name}: not in the report; regenerate it");
                continue;
            }
            Lost("correct");
            Lost("confident_correct");
            var newlyWrong = Slots(now["confident_wrong"]).Except(Slots(was["confident_wrong"])).ToList();
            if (newlyWrong.Count > 0)
                problems.Add($"{name}: now confidently wrong in slot(s) {string.Join(", ", newlyWrong)}");
            if ((bool?)was["self_correct"] == true && (bool?)now["self_correct"] == false)
                problems.Add($"{name}: no longer finds you");

            void Lost(string key)
            {
                var lost = Slots(was[key]).Except(Slots(now[key])).ToList();
                if (lost.Count > 0)
                    problems.Add($"{name}: no longer {key.Replace('_', ' ')} in slot(s) {string.Join(", ", lost)}");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems) + $"\n(see {report})");
    }

    private static IEnumerable<int> Slots(JsonNode? node) => node?.AsArray().Select(slot => (int)slot!) ?? [];
}
