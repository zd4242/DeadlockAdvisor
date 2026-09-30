using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Tests.Support;

/// <summary>A labelled capture: an image and what's in it.</summary>
public sealed record CorpusCapture(string Name, LabeledCapture Labels, RgbImage Image)
{
    /// <summary>Captures of one match share a label, so a whole match can be held out.</summary>
    public string Match => Labels.Match ?? Name;

    /// <summary>Every image/label pair in a folder, in name order.</summary>
    public static List<CorpusCapture> LoadAll(string folder, string pattern = "*.json") =>
        Directory.GetFiles(folder, pattern)
            .Order(StringComparer.Ordinal)
            .Where(json => File.Exists(Path.ChangeExtension(json, ".png")))
            .Select(json => new CorpusCapture(Path.GetFileNameWithoutExtension(json),
                LabeledCapture.FromJson(JsonNode.Parse(File.ReadAllText(json))!), ImageFile.Load(Path.ChangeExtension(json, ".png"))))
            .ToList();
}

/// <summary>One labelled slot, as detection read it.</summary>
/// <param name="TruthRank">Where the true hero came in the slot's scores before assignment, 0 being first.</param>
/// <param name="BestImpostor">The best score of any other hero in the slot, before assignment.</param>
public sealed record SlotOutcome(string Capture, int Slot, string Truth, SlotState State, string? Read, bool Confident,
    double Score, double Margin, int TruthRank, double TruthScore, double BestImpostor, string? BestImpostorHero, Box Box, Box GridBox)
{
    public bool Correct => Read == Truth;
    public bool ConfidentWrong => Confident && Read is not null && Read != Truth;

    /// <summary>How far the true hero's own score clears every other hero's: below zero, no assignment can save it.</summary>
    public double TrueMargin => TruthScore - BestImpostor;
}

public sealed record CaptureOutcome(CorpusCapture Capture, Detection Detection, IReadOnlyList<SlotOutcome> Slots, long Milliseconds)
{
    public bool? SelfCorrect => Capture.Labels.SelfSlot is { } self ? Detection.SelfSlot == self : null;

    /// <summary>Whether applying the read as-is would have been right, apart from slots nothing could read.</summary>
    public bool NeedsNoCorrection => Slots.Where(slot => slot.State != SlotState.Dead).All(slot => slot.Correct) && SelfCorrect != false;
}

/// <summary>
/// Detection measured against labelled captures: accuracy per portrait state, how clear the true
/// hero's lead is, what gets confused with what, and where each hero's art sits in its slot.
/// </summary>
public static class VisionEval
{
    public static List<CaptureOutcome> Run(IEnumerable<CorpusCapture> captures, TemplateBank bank) =>
        captures.Select(capture => Run(capture, bank)).ToList();

    public static CaptureOutcome Run(CorpusCapture capture, TemplateBank bank)
    {
        var clock = Stopwatch.StartNew();
        var detection = Detector.Detect(capture.Image, bank, capture.Labels.Grid, screenHeight: capture.Labels.ScreenHeight)
                        ?? throw new InvalidOperationException($"{capture.Name}: no strip found");
        clock.Stop();

        var boxes = detection.Geometry.Boxes();
        var slots = new List<SlotOutcome>();
        foreach (var (slot, truth) in capture.Labels.Heroes.OrderBy(pair => pair.Key))
        {
            var row = detection.Scores[slot];
            var truthIndex = detection.Heroes.ToList().IndexOf(truth);
            var truthScore = truthIndex >= 0 ? row[truthIndex] : -1.0;
            var impostor = Enumerable.Range(0, row.Length).Where(hero => hero != truthIndex).MaxBy(hero => row[hero]);
            var reading = detection.Slots[slot];
            slots.Add(new SlotOutcome(capture.Name, slot, truth, capture.Labels.StateOf(slot), reading.HeroId, reading.IsConfident,
                reading.Score, reading.Margin, truthIndex >= 0 ? row.Count(score => score > truthScore) : row.Length, truthScore,
                row[impostor], detection.Heroes[impostor], reading.Box, boxes[slot]));
        }
        return new CaptureOutcome(capture, detection, slots, clock.ElapsedMilliseconds);
    }

    private sealed record Tally(int Slots, int Correct, int ConfidentCorrect, int ConfidentWrong, int Unread)
    {
        public static Tally Of(IEnumerable<SlotOutcome> outcomes)
        {
            var list = outcomes.ToList();
            return new Tally(list.Count, list.Count(o => o.Correct), list.Count(o => o.Correct && o.Confident),
                list.Count(o => o.ConfidentWrong), list.Count(o => o.Read is null));
        }

        public JsonObject ToJson() => new()
        {
            ["slots"] = Slots,
            ["correct"] = Correct,
            ["confident_correct"] = ConfidentCorrect,
            ["confident_wrong"] = ConfidentWrong,
            ["unread"] = Unread,
        };
    }

    /// <summary>
    /// What the corpus test pins, slot by slot: which were read right, which confidently, and which
    /// confidently wrong. A change may add to the first two and take from the third, never the reverse.
    /// </summary>
    public static JsonObject Pinned(IReadOnlyList<CaptureOutcome> outcomes)
    {
        var slots = outcomes.SelectMany(outcome => outcome.Slots).ToList();
        var summary = new JsonObject { ["all"] = Tally.Of(slots).ToJson() };
        foreach (var state in Enum.GetValues<SlotState>())
        {
            if (slots.Any(slot => slot.State == state))
                summary[Name(state)] = Tally.Of(slots.Where(slot => slot.State == state)).ToJson();
        }
        summary["captures_needing_no_correction"] = outcomes.Count(outcome => outcome.NeedsNoCorrection);
        summary["captures"] = outcomes.Count;

        var captures = new JsonObject();
        foreach (var outcome in outcomes)
        {
            captures[outcome.Capture.Name] = new JsonObject
            {
                ["correct"] = Slots(outcome.Slots.Where(slot => slot.Correct)),
                ["confident_correct"] = Slots(outcome.Slots.Where(slot => slot.Correct && slot.Confident)),
                ["confident_wrong"] = Slots(outcome.Slots.Where(slot => slot.ConfidentWrong)),
                ["self_correct"] = outcome.SelfCorrect,
            };
        }
        return new JsonObject { ["summary"] = summary, ["captures"] = captures };

        static JsonArray Slots(IEnumerable<SlotOutcome> chosen) => new(chosen.Select(slot => (JsonNode?)slot.Slot).ToArray());
    }

    /// <summary>The report to read: everything <see cref="Pinned"/> keeps, and what explains it.</summary>
    public static string Markdown(IReadOnlyList<CaptureOutcome> outcomes, TemplateBank bank)
    {
        var slots = outcomes.SelectMany(outcome => outcome.Slots).ToList();
        var text = new StringBuilder();
        text.AppendLine(Invariant($"# Detection report — {outcomes.Count} captures, {slots.Count} labelled slots, {bank.Vectors.Count} reference images"));
        text.AppendLine();
        text.Append(Invariant($"Captures needing no correction: {outcomes.Count(o => o.NeedsNoCorrection)} of {outcomes.Count}. "))
            .Append(Invariant($"You found: {outcomes.Count(o => o.SelfCorrect == true)} of {outcomes.Count(o => o.SelfCorrect is not null)}. "))
            .AppendLine(Invariant($"Detection took {outcomes.Sum(o => o.Milliseconds)} ms in all, {outcomes.Average(o => (double)o.Milliseconds):0} ms each."));
        text.AppendLine();
        text.AppendLine("| state | slots | correct | confident & correct | confident & wrong | unread | true margin p5 | median |");
        text.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var (name, group) in new[] { ("all", slots) }
                     .Concat(Enum.GetValues<SlotState>().Select(state => (Name(state), slots.Where(slot => slot.State == state).ToList())))
                     .Where(entry => entry.Item2.Count > 0))
        {
            var tally = Tally.Of(group);
            var margins = group.Select(slot => slot.TrueMargin).Order().ToList();
            text.Append(Invariant($"| {name} | {tally.Slots} | {Percent(tally.Correct, tally.Slots)} | {Percent(tally.ConfidentCorrect, tally.Slots)} | "))
                .AppendLine(Invariant($"{tally.ConfidentWrong} | {tally.Unread} | {Quantile(margins, 0.05):+0.000;-0.000} | {Quantile(margins, 0.5):+0.000;-0.000} |"));
        }

        text.AppendLine();
        text.AppendLine("## Misreads");
        text.AppendLine();
        text.AppendLine("| capture | slot | state | truth | read | score | margin | confident | truth rank | true margin |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var slot in slots.Where(slot => !slot.Correct))
        {
            text.Append(Invariant($"| {slot.Capture} | {slot.Slot} | {Name(slot.State)} | {slot.Truth} | {slot.Read ?? "—"} | {slot.Score:0.000} | "))
                .AppendLine(Invariant($"{slot.Margin:0.000} | {(slot.Confident ? "**yes**" : "no")} | {slot.TruthRank} | {slot.TrueMargin:+0.000;-0.000} |"));
        }

        text.AppendLine();
        text.AppendLine("## Confusions (truth → what beat it)");
        text.AppendLine();
        foreach (var group in slots.Where(slot => slot.TrueMargin < 0).GroupBy(slot => $"{slot.Truth} → {slot.BestImpostorHero}")
                     .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal))
            text.AppendLine(Invariant($"- {group.Key}: {group.Count()}"));

        text.AppendLine();
        text.AppendLine("## Hub heroes (mean score in slots that aren't theirs)");
        text.AppendLine();
        var impostorScores = new Dictionary<string, List<double>>();
        foreach (var outcome in outcomes)
        {
            foreach (var slot in outcome.Slots)
            {
                var row = outcome.Detection.Scores[slot.Slot];
                for (var hero = 0; hero < row.Length; hero++)
                {
                    var id = outcome.Detection.Heroes[hero];
                    if (id == slot.Truth)
                        continue;
                    if (!impostorScores.TryGetValue(id, out var list))
                        impostorScores[id] = list = [];
                    list.Add(row[hero]);
                }
            }
        }
        foreach (var (hero, scores) in impostorScores.OrderByDescending(pair => pair.Value.Average()).Take(8))
            text.AppendLine(Invariant($"- {hero}: {scores.Average():0.000}"));

        text.AppendLine();
        text.AppendLine("## Where each hero's art sits (correct reads; offsets as fractions of the grid box)");
        text.AppendLine();
        text.AppendLine("| hero | n | dx | dy | scale |");
        text.AppendLine("|---|---|---|---|---|");
        foreach (var group in slots.Where(slot => slot.Correct).GroupBy(slot => slot.Truth).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var dx = group.Select(slot => (slot.Box.CenterX - slot.GridBox.CenterX) / slot.GridBox.W).Order().ToList();
            var dy = group.Select(slot => (slot.Box.Y - slot.GridBox.Y) / slot.GridBox.H).Order().ToList();
            var scale = group.Select(slot => slot.Box.W / slot.GridBox.W).Order().ToList();
            text.AppendLine(Invariant($"| {group.Key} | {group.Count()} | {Quantile(dx, 0.5):+0.00;-0.00} | {Quantile(dy, 0.5):+0.00;-0.00} | {Quantile(scale, 0.5):0.00} |"));
        }
        return text.ToString();
    }

    private static string Name(SlotState state) => state.ToString().ToLowerInvariant();

    private static string Percent(int part, int whole) => whole == 0 ? "—" : Invariant($"{part} ({100.0 * part / whole:0}%)");

    private static double Quantile(IReadOnlyList<double> sorted, double q) =>
        sorted.Count == 0 ? double.NaN : sorted[(int)Math.Clamp(Math.Floor(q * (sorted.Count - 1)), 0, sorted.Count - 1)];

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
