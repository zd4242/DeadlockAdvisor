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
        var report = UiHarness.MockupPath("detection_report.md");
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

    /// <summary>
    /// Detecting again later in a match that's been applied: each match's first capture is applied as
    /// labelled, and every later one of the same match then reads with nothing left to check: the
    /// dead, the faded and Silver's wolf form kept from the match, and you found or carried over.
    /// </summary>
    [Fact]
    public void LaterCapturesOfAnAppliedMatchNeedNoChecking()
    {
        var replayed = 0;
        foreach (var match in _outcomes.Value.GroupBy(outcome => outcome.Capture.Match).Where(group => group.Count() > 1))
        {
            var first = match.First().Capture.Labels;
            foreach (var later in match.Skip(1))
            {
                var kept = RosterContinuity.Apply(later.Detection, first.Heroes, first.SelfSlot);
                var labels = later.Capture.Labels;
                Assert.All(labels.Heroes, pair => Assert.Equal(pair.Value, kept.HeroAt(pair.Key)));
                Assert.All(kept.Slots, slot => Assert.True(slot.IsSettled, $"{later.Capture.Name} slot {slot.Index} unsettled"));
                if (labels.SelfSlot is { } self && first.SelfSlot is not null)
                    Assert.Equal(self, kept.SelfSlot);
                replayed++;
            }
        }
        Assert.True(replayed >= 8, $"only {replayed} captures replayed");
    }

    /// <summary>Every labelled capture: the corpus, and the fixtures (which label only the slots they're sure of).</summary>
    public static List<CorpusCapture> Labelled() =>
        [.. CorpusCapture.LoadAll(Golden.PathOf("vision", "corpus")), .. CorpusCapture.LoadAll(Golden.PathOf("vision", "fixtures"))];

    /// <summary>
    /// The top bar crops every hero's card the same way, which is what lets every portrait be cut at
    /// <see cref="TopbarDerivation.InGameFrame"/> and looked for in the same box. Measured afresh off
    /// every labelled capture (listed in mockups/template_frames.md): if Valve ever crops differently,
    /// the cut portraits stop sitting where the grid says, and this says so.
    /// </summary>
    [Fact]
    public void TheGameCropsEveryCardTheSameWay()
    {
        var bank = Bank.Where(source => source is { Kind: TemplateKind.Derived, State: PortraitState.Normal });
        var (width, frames) = VisionCalibration.Fit(VisionCalibration.Measure(Labelled(), bank));

        var report = new System.Text.StringBuilder(FormattableString.Invariant(
            $"# Where each cut portrait sits: {frames.Count} heroes, {width:0.000} pitches wide\n\n| hero | n | dx | dy | scale |\n|---|---|---|---|---|\n"));
        foreach (var (row, (frame, n)) in frames.OrderBy(pair => bank.Sources[pair.Key].Hero, StringComparer.Ordinal))
            report.AppendLine(FormattableString.Invariant($"| {bank.Sources[row].Hero} | {n} | {frame.Dx:+0.000;-0.000} | {frame.Dy:+0.000;-0.000} | {frame.Scale:0.000} |"));
        File.WriteAllText(UiHarness.MockupPath("template_frames.md"), report.ToString());

        Assert.True(Math.Abs(width - TopbarDerivation.WidthRatio) < 0.01, $"portraits are {width:0.000} pitches wide, not {TopbarDerivation.WidthRatio}");
        var measured = frames.Where(pair => pair.Value.Samples >= 3).ToList();
        Assert.True(measured.Count >= 25, $"only {measured.Count} heroes measured");
        Assert.All(measured, pair => Assert.True(
            Math.Abs(pair.Value.Frame.Dx) < 0.04 && Math.Abs(pair.Value.Frame.Dy) < 0.04 && Math.Abs(pair.Value.Frame.Scale / width - 1) < 0.05,
            $"{bank.Sources[pair.Key].Hero} sits at {pair.Value.Frame}"));
    }
}
