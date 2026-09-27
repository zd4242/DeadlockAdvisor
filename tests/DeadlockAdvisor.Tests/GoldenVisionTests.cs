using System.Text.Json.Nodes;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Vision;
using static DeadlockAdvisor.Tests.Support.Golden;
using static DeadlockAdvisor.Tests.Support.VisionData;

namespace DeadlockAdvisor.Tests;

/// <summary>Screen detection against what the Python app read off the same images.</summary>
public class GoldenVisionTests
{
    private const float DescriptorTolerance = 1e-4f;

    private static float[] Floats(JsonNode? node)
    {
        var bytes = Convert.FromBase64String(Text(node));
        var values = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static void AssertVectorClose(float[] expected, float[] actual, string because)
    {
        Assert.Equal(expected.Length, actual.Length);
        var worst = 0f;
        for (var i = 0; i < expected.Length; i++)
            worst = Math.Max(worst, Math.Abs(expected[i] - actual[i]));
        Assert.True(worst <= DescriptorTolerance, $"{because}: descriptors differ by up to {worst}");
    }

    [Fact]
    public void ResizeMatchesPillowByteForByte()
    {
        foreach (var testCase in Items(Json("vision/resize.json")))
        {
            var source = Text(testCase["source"]);
            var image = source.StartsWith("fixtures/") ? Image(source) : ImageFile.Load(Path.Combine(TopbarDir, source));
            if (testCase["box"] is JsonArray box)
                image = image.Crop((int)box[0]!, (int)box[1]!, (int)box[2]!, (int)box[3]!);
            var resized = ImageOps.ResizeRgb(image, ImageOps.GridW, ImageOps.GridH);
            var expected = Convert.FromBase64String(Text(testCase["pixels"]));
            var differ = Enumerable.Range(0, expected.Length).Where(i => expected[i] != resized.Pixels[i]).ToList();
            Assert.True(differ.Count == 0,
                $"{source} {testCase["box"]?.ToJsonString()} ({image.Width}×{image.Height}): {differ.Count} bytes differ from Pillow's, "
                + string.Join(", ", differ.Take(8).Select(i => $"[{i / 3 % ImageOps.GridW},{i / 3 / ImageOps.GridW}].{i % 3} {expected[i]} vs {resized.Pixels[i]}")));
        }
    }

    [Fact]
    public void TemplateBankMatches()
    {
        var golden = Json("vision/templates.json");
        var bank = Bank;
        Assert.Equal(Items(golden["heroes"]).Select(Text), bank.Heroes);
        Assert.Equal(Items(golden["rows_hero"]).Select(node => (int)node), bank.RowsHero);
        Assert.Equal(Items(golden["sources"]).Select(Text),
            bank.Sources.Select(source => Path.GetRelativePath(TopbarDir, source).Replace('\\', '/')));

        var dim = (int)golden["dim"]!;
        Assert.Equal(ImageOps.Dimensions, dim);
        var vectors = Floats(golden["vectors"]);
        for (var row = 0; row < bank.Vectors.Count; row++)
            AssertVectorClose(vectors.AsSpan(row * dim, dim).ToArray(), bank.Vectors[row], bank.Sources[row]);
    }

    public static TheoryData<string> DetectionImages() =>
        new(Items(Json("vision/detections.json")).Select(testCase => Text(testCase["image"])));

    [Theory]
    [MemberData(nameof(DetectionImages))]
    public void DetectionMatches(string name)
    {
        var testCase = Items(Json("vision/detections.json")).Single(entry => Text(entry["image"]) == name);
        var image = Image(name);
        var bounds = testCase["bounds"]!;
        var pitchRange = Range(bounds["pitch_range"]);
        var topRange = Range(bounds["top_range"]);
        var screenHeight = bounds["screen_height"] is { } height ? (int?)height : null;

        var search = Layout.Search(image, Bank, pitchRange, topRange, screenHeight);
        if (testCase["search"] is not JsonObject expectedSearch)
        {
            Assert.Null(search);
            return;
        }
        Assert.NotNull(search);
        AssertGeometry(expectedSearch["geometry"], search.Value.Geometry, $"{name} search");
        AssertEx.Close(Number(expectedSearch["score"]), search.Value.Score, 1e-3, $"{name} search score");

        var detection = Detector.Detect(image, Bank, search.Value.Geometry);
        AssertDetection(testCase["detection"], detection, name);

        foreach (var (reading, expected) in detection!.Slots.Zip(Items(testCase["slot_descriptors"]).Cast<JsonNode?>()))
        {
            var box = ExpectedBox(testCase["detection"]!["slots"]![reading.Index]!["box"]);
            var crop = Layout.Crop(image, box);
            var vector = crop is null ? null : ImageOps.Descriptor(crop);
            if (expected is null)
                Assert.Null(vector);
            else
                AssertVectorClose(Floats(expected), vector!, $"{name} slot {reading.Index}");
        }

        var cached = Detector.Detect(image, Bank, Geometry.FromJson(testCase["detection"]!["geometry"]!.AsObject()));
        AssertDetection(testCase["cached_detection"], cached, $"{name} (cached grid)");
    }

    private static (double, double)? Range(JsonNode? node) =>
        node is JsonArray pair ? (Number(pair[0]), Number(pair[1])) : null;

    private static Box ExpectedBox(JsonNode? node) =>
        new(Number(node![0]), Number(node[1]), Number(node[2]), Number(node[3]));

    private static void AssertGeometry(JsonNode? expected, Geometry actual, string because)
    {
        var golden = Geometry.FromJson(expected!.AsObject())!;
        AssertEx.Close(golden.CenterX, actual.CenterX, 1e-4, $"{because}: center_x");
        AssertEx.Close(golden.Pitch, actual.Pitch, 1e-4, $"{because}: pitch");
        AssertEx.Close(golden.Top, actual.Top, 1e-4, $"{because}: top");
        AssertEx.Close(golden.GapRatio, actual.GapRatio, 1e-4, $"{because}: gap_ratio");
        AssertEx.Close(golden.WidthRatio, actual.WidthRatio, 1e-4, $"{because}: width_ratio");
    }

    private static void AssertDetection(JsonNode? expected, Detection? actual, string because)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }
        Assert.NotNull(actual);
        AssertGeometry(expected["geometry"], actual.Geometry, because);

        var slots = Items(expected["slots"]).ToList();
        Assert.Equal(slots.Count, actual.Slots.Count);
        foreach (var (golden, reading) in slots.Zip(actual.Slots))
        {
            var slot = $"{because} slot {reading.Index}";
            Assert.Equal((int)golden["index"]!, reading.Index);
            Assert.True(golden["hero_id"]?.GetValue<string>() == reading.HeroId, $"{slot}: hero {golden["hero_id"]} vs {reading.HeroId}");
            Assert.True(golden["runner_up"]?.GetValue<string>() == reading.RunnerUp, $"{slot}: runner-up {golden["runner_up"]} vs {reading.RunnerUp}");
            Assert.Equal((bool)golden["is_confident"]!, reading.IsConfident);
            AssertEx.Close(Number(golden["score"]), reading.Score, 1e-4, $"{slot}: score");
            AssertEx.Close(Number(golden["margin"]), reading.Margin, 1e-4, $"{slot}: margin");
            var box = ExpectedBox(golden["box"]);
            AssertEx.Close(box.X, reading.Box.X, 1e-6, $"{slot}: box x");
            AssertEx.Close(box.Y, reading.Box.Y, 1e-6, $"{slot}: box y");
            AssertEx.Close(box.W, reading.Box.W, 1e-6, $"{slot}: box w");
            Assert.Equal(Items(golden["ranked"]).Select(pair => Text(pair[0])), reading.Ranked.Select(pair => pair.HeroId));
        }

        Assert.Equal((int?)expected["self_slot"], actual.SelfSlot);
        AssertEx.Close(Number(expected["self_score"]), actual.SelfScore, 1e-4, $"{because}: self score");
        foreach (var (golden, value) in Items(expected["self_scores"]).Zip(actual.SelfScores))
            AssertEx.Close(Number(golden), value, 1e-4, $"{because}: self scores");
        Assert.Equal((int)expected["confident_count"]!, actual.ConfidentCount);
    }

    [Fact]
    public void GeometryFitsMatch()
    {
        var golden = Json("vision/layout.json");
        foreach (var fit in Items(golden["fits"]))
        {
            var centers = Items(fit["centers"]).Select(node => (double?)Number(node)).ToList();
            var weights = Items(fit["weights"]).Select(Number).ToList();
            var guess = Geometry.FromJson(fit["guess"]!.AsObject())!;
            var boxes = Items(fit["boxes"]).Select(ExpectedBox).ToList();

            var fitted = Layout.FitGeometry(centers, weights, guess);
            AssertGeometry(fit["fit_geometry"], fitted, "fit_geometry");
            AssertGeometry(fit["fit_shape"], Layout.FitShape(boxes, weights, fitted), "fit_shape");
        }
    }

    [Fact]
    public void TrimmedScoresCandidatesAndSelfSlotMatch()
    {
        var golden = Json("vision/layout.json");
        foreach (var pair in Items(golden["trimmed"]))
        {
            var values = Items(pair[0]).Select(node => (float)Number(node)).ToList();
            AssertEx.Close(Number(pair[1]), Layout.TrimmedScore(values), 1e-6);
        }

        foreach (var entry in Items(golden["candidates"]))
        {
            var range = Range(entry[2])!.Value;
            var candidates = Layout.Candidates((int)entry[0]!, (int)entry[1]!, range, Number(entry[3]));
            var expected = Items(entry[4]).ToList();
            Assert.Equal(expected.Count, candidates.Count);
            foreach (var (geometry, candidate) in expected.Zip(candidates))
                AssertGeometry(geometry, candidate, "candidates");
        }

        foreach (var pair in Items(golden["self_slot"]))
        {
            var (slot, score) = Detector.FindSelfSlot(Items(pair[0]).Select(Number).ToList());
            Assert.Equal((int?)pair[1]![0], slot);
            AssertEx.Close(Number(pair[1]![1]), score);
        }
    }
}
