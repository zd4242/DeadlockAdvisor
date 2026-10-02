using System.Text.Json.Nodes;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Vision;
using static DeadlockAdvisor.Tests.Support.Golden;
using static DeadlockAdvisor.Tests.Support.VisionData;

namespace DeadlockAdvisor.Tests;

/// <summary>
/// Screen detection against its pinned outputs, regenerated after a deliberate change
/// (DEADLOCK_UPDATE_GOLDENS=1).
/// </summary>
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
        var bank = Bank;
        if (Updating)
        {
            var bytes = new byte[bank.Vectors.Count * ImageOps.Dimensions * sizeof(float)];
            for (var row = 0; row < bank.Vectors.Count; row++)
                Buffer.BlockCopy(bank.Vectors[row], 0, bytes, row * ImageOps.Dimensions * sizeof(float), ImageOps.Dimensions * sizeof(float));
            WriteJson("vision/templates.json", new JsonObject
            {
                ["heroes"] = new JsonArray(bank.Heroes.Select(hero => (JsonNode?)hero).ToArray()),
                ["rows_hero"] = new JsonArray(bank.RowsHero.Select(hero => (JsonNode?)hero).ToArray()),
                ["sources"] = new JsonArray(bank.Sources.Select(source => (JsonNode?)RelativeSource(source)).ToArray()),
                ["dim"] = ImageOps.Dimensions,
                ["vectors"] = Convert.ToBase64String(bytes),
            });
            return;
        }

        var golden = Json("vision/templates.json");
        Assert.Equal(Items(golden["heroes"]).Select(Text), bank.Heroes);
        Assert.Equal(Items(golden["rows_hero"]).Select(node => (int)node), bank.RowsHero);
        Assert.Equal(Items(golden["sources"]).Select(Text), bank.Sources.Select(RelativeSource));

        var dim = (int)golden["dim"]!;
        Assert.Equal(ImageOps.Dimensions, dim);
        var vectors = Floats(golden["vectors"]);
        for (var row = 0; row < bank.Vectors.Count; row++)
            AssertVectorClose(vectors.AsSpan(row * dim, dim).ToArray(), bank.Vectors[row], bank.Sources[row].Path);
    }

    private static string RelativeSource(TemplateSource source) => Path.GetRelativePath(TopbarDir, source.Path).Replace('\\', '/');

    public static TheoryData<string> DetectionImages() =>
        new(Items(Json("vision/detections.json")).Select(testCase => Text(testCase["image"])));

    /// <summary>A search, the detection off the grid it found, and a detection off that grid as if cached.</summary>
    private static ((Geometry Geometry, double Score)? Search, Detection? Detection, Detection? Cached) Run(JsonNode testCase)
    {
        var image = Image(Text(testCase["image"]));
        var bounds = testCase["bounds"]!;
        var screenHeight = bounds["screen_height"] is { } height ? (int?)height : null;
        var search = Layout.Search(image, Bank, Range(bounds["pitch_range"]), Range(bounds["top_range"]), screenHeight);
        if (search is null)
            return (null, null, null);
        var detection = Detector.Detect(image, Bank, search.Value.Geometry);
        var cached = detection is null ? null : Detector.Detect(image, Bank, detection.Geometry);
        return (search, detection, cached);
    }

    [Theory]
    [MemberData(nameof(DetectionImages))]
    public void DetectionMatches(string name)
    {
        if (Updating)
            return;
        var testCase = Items(Json("vision/detections.json")).Single(entry => Text(entry["image"]) == name);
        var (search, detection, cached) = Run(testCase);
        if (testCase["search"] is not JsonObject expectedSearch)
        {
            Assert.Null(search);
            return;
        }
        Assert.NotNull(search);
        AssertGeometry(expectedSearch["geometry"], search.Value.Geometry, $"{name} search");
        AssertEx.Close(Number(expectedSearch["score"]), search.Value.Score, 1e-3, $"{name} search score");
        AssertDetection(testCase["detection"], detection, name);
        AssertDetection(testCase["cached_detection"], cached, $"{name} (cached grid)");
    }

    /// <summary>With DEADLOCK_UPDATE_GOLDENS=1, rewrite detections.json from what this app reads now.</summary>
    [Fact]
    public void DetectionGoldensAreRegenerated()
    {
        if (!Updating)
            return;
        var cases = new JsonArray();
        foreach (var testCase in Items(Json("vision/detections.json")))
        {
            var (search, detection, cached) = Run(testCase);
            cases.Add(new JsonObject
            {
                ["image"] = Text(testCase["image"]),
                ["bounds"] = testCase["bounds"]!.DeepClone(),
                ["search"] = search is { } found ? new JsonObject { ["geometry"] = found.Geometry.ToJson(), ["score"] = found.Score } : null,
                ["detection"] = DetectionJson(detection),
                ["cached_detection"] = DetectionJson(cached),
            });
        }
        WriteJson("vision/detections.json", cases);
    }

    private static JsonObject? DetectionJson(Detection? detection) => detection is null ? null : new JsonObject
    {
        ["geometry"] = detection.Geometry.ToJson(),
        ["self_slot"] = detection.SelfSlot,
        ["confident_count"] = detection.ConfidentCount,
        ["slots"] = new JsonArray(detection.Slots.Select(reading => (JsonNode?)new JsonObject
        {
            ["hero_id"] = reading.HeroId,
            ["runner_up"] = reading.RunnerUp,
            ["is_confident"] = reading.IsConfident,
            ["ranked"] = new JsonArray(reading.Ranked.Select(pair => (JsonNode?)pair.HeroId).ToArray()),
        }).ToArray()),
    };

    private static (double, double)? Range(JsonNode? node) =>
        node is JsonArray pair ? (Number(pair[0]), Number(pair[1])) : null;

    private static Box ExpectedBox(JsonNode? node) =>
        new(Number(node![0]), Number(node[1]), Number(node[2]), Number(node[3]));

    private static void AssertGeometry(JsonNode? expected, Geometry actual, string because)
    {
        var golden = Geometry.Parse(expected!.AsObject())!;
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
            Assert.True(golden["hero_id"]?.GetValue<string>() == reading.HeroId, $"{slot}: hero {golden["hero_id"]} vs {reading.HeroId}");
            Assert.True(golden["runner_up"]?.GetValue<string>() == reading.RunnerUp, $"{slot}: runner-up {golden["runner_up"]} vs {reading.RunnerUp}");
            Assert.True((bool)golden["is_confident"]! == reading.IsConfident, $"{slot}: confident {golden["is_confident"]} vs {reading.IsConfident}");
            Assert.Equal(Items(golden["ranked"]).Select(Text), reading.Ranked.Select(pair => pair.HeroId));
        }

        Assert.Equal((int?)expected["self_slot"], actual.SelfSlot);
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
            var guess = Geometry.Parse(fit["guess"]!.AsObject())!;
            var boxes = Items(fit["boxes"]).Select(ExpectedBox).ToList();

            var fitted = Layout.FitGeometry(centers, weights, guess);
            AssertGeometry(fit["fit_geometry"], fitted, "fit_geometry");
            AssertGeometry(fit["fit_shape"], Layout.FitShape(boxes, weights, fitted), "fit_shape");
        }
    }

    [Fact]
    public void TrimmedScoresAndCandidatesMatch()
    {
        var golden = Json("vision/layout.json");
        foreach (var pair in Items(golden["trimmed"]))
        {
            var values = Items(pair[0]).Select(node => (float)Number(node)).ToList();
            AssertEx.Close(Number(pair[1]), Layout.TrimmedScore(values), 1e-6);
        }

        if (Updating)
        {
            foreach (var entry in Items(golden["candidates"]))
            {
                var candidates = Layout.Candidates((int)entry[0]!, (int)entry[1]!, Range(entry[2])!.Value, Number(entry[3]));
                entry.AsArray()[4] = new JsonArray(candidates.Select(candidate => (JsonNode?)candidate.ToJson()).ToArray());
            }
            golden.AsObject().Remove("self_slot");
            WriteJson("vision/layout.json", golden);
            return;
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
    }
}
