using System.Text.Json.Nodes;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Vision;
using static DeadlockAdvisor.Tests.Support.VisionData;

namespace DeadlockAdvisor.Tests;

/// <summary>
/// The screen-detection pipeline, ported from test_vision.py. The role and assignment logic
/// is pure arithmetic and tested without images; the rest runs against the golden captures.
/// </summary>
public class VisionTests
{
    private static readonly List<string?> _heroes = Enumerable.Range(0, 12).Select(i => (string?)$"h{i}").ToList();

    // -- role arithmetic (no images) -------------------------------------------

    [Fact]
    public void TeamsSplitOnWhicheverSideYouAre()
    {
        var roles = VisionApply.RolesFor(_heroes, 2);
        Assert.Equal(Role.Self, roles["h2"]);
        Assert.All([0, 1, 3, 4, 5], i => Assert.Equal(Role.Ally, roles[$"h{i}"]));
        Assert.All(Enumerable.Range(6, 6), i => Assert.Equal(Role.Enemy, roles[$"h{i}"]));

        roles = VisionApply.RolesFor(_heroes, 8);
        Assert.Equal(Role.Self, roles["h8"]);
        Assert.Equal(Role.Enemy, roles["h0"]);
        Assert.Equal(Role.Ally, roles["h9"]);
    }

    [Fact]
    public void NothingIsAssignedWhenSelfIsUnknown()
    {
        Assert.Empty(VisionApply.RolesFor(_heroes, null));
    }

    [Fact]
    public void ApplyWritesTheRoles()
    {
        var match = new MatchState();
        var count = VisionApply.ApplyToMatch(match, _heroes, 2, _heroes!);
        Assert.Equal(12, count);
        Assert.Equal("h2", match.SelfHero);
        Assert.Equal(["h0", "h1", "h3", "h4", "h5"], match.Allies.Order());
        Assert.Equal(6, match.Enemies.Count);
    }

    [Fact]
    public void ApplyDropsHeroesThatAreNotInTheCsv()
    {
        var match = new MatchState();
        var count = VisionApply.ApplyToMatch(match, _heroes, 0, ["h0", "h6"]);
        Assert.Equal(2, count);
        Assert.Equal("h0", match.SelfHero);
        Assert.Equal(["h6"], match.Enemies);
    }

    [Fact]
    public void UnreadableSlotsLeaveGapsRatherThanGuesses()
    {
        var match = new MatchState();
        var heroes = _heroes.ToList();
        heroes[4] = null;
        heroes[9] = null;
        VisionApply.ApplyToMatch(match, heroes, 2, heroes.OfType<string>());
        Assert.Equal(4, match.Allies.Count);
        Assert.Equal(5, match.Enemies.Count);
    }

    // -- assignment ------------------------------------------------------------

    [Fact]
    public void AssignmentNeverRepeatsAHero()
    {
        var result = Matcher.Assign([[0.90f, 0.80f, 0.10f], [0.85f, 0.20f, 0.15f]], minScore: 0f);
        Assert.Equal([0, 1], result.Select(r => r.HeroIndex));
    }

    [Fact]
    public void AssignmentGivesAHeroToWhoeverIsSurest()
    {
        var result = Matcher.Assign([[0.50f, 0.49f], [0.95f, 0.10f]], minScore: 0f);
        Assert.Equal(0, result[1].HeroIndex);
        Assert.Equal(1, result[0].HeroIndex);
    }

    [Fact]
    public void WeakSlotsAreLeftUnassigned()
    {
        var result = Matcher.Assign([[0.80f, 0.10f], [0.05f, 0.04f]], minScore: 0.30f);
        Assert.Equal(0, result[0].HeroIndex);
        Assert.Null(result[1].HeroIndex);
    }

    // -- geometry --------------------------------------------------------------

    [Fact]
    public void SlotCentresAreEvenlyPitchedAndMirrored()
    {
        var centers = new Geometry(960.0, 100.0, 10.0, GapRatio: 1.5).Centers();
        Assert.Equal(12, centers.Count);
        for (var i = 0; i < 5; i++)
            Assert.Equal(100.0, centers[i + 1] - centers[i], 6);
        for (var i = 0; i < 6; i++)
            Assert.True(Math.Abs((960.0 - centers[i]) - (centers[11 - i] - 960.0)) < 1e-6);
    }

    [Fact]
    public void GeometrySurvivesARoundTripThroughSettings()
    {
        var original = new Geometry(960.0, 126.5, 11.25, 1.68, 0.61);
        Assert.Equal(original, Geometry.FromJson(JsonNode.Parse(original.ToJson().ToJsonString())!.AsObject()));
        Assert.Null(Geometry.FromJson(new JsonObject { ["nonsense"] = 1 }));
    }

    [Fact]
    public void AStaleCachedGridIsRejected()
    {
        var payload = new Geometry(960.0, 126.5, 11.25, 1.68, 0.61).ToJson();
        Assert.Equal(Layout.CacheVersion, (int)payload["version"]!);

        var stale = payload.DeepClone().AsObject();
        stale["version"] = Layout.CacheVersion - 1;
        Assert.Null(Geometry.FromJson(stale));
        var unversioned = payload.DeepClone().AsObject();
        unversioned.Remove("version");
        Assert.Null(Geometry.FromJson(unversioned));
    }

    [Fact]
    public void RefitRecoversAShiftedGrid()
    {
        var truth = new Geometry(970.0, 120.0, 8.0, 1.70);
        var guess = new Geometry(950.0, 112.0, 8.0, 1.50);
        var refit = Layout.FitGeometry(truth.Centers().Select(c => (double?)c).ToList(), Enumerable.Repeat(1.0, 12).ToList(), guess);
        Assert.True(Math.Abs(refit.CenterX - 970.0) < 0.5, $"{refit.CenterX}");
        Assert.True(Math.Abs(refit.Pitch - 120.0) < 0.5, $"{refit.Pitch}");
        Assert.True(Math.Abs(refit.GapRatio - 1.70) < 0.02, $"{refit.GapRatio}");
    }

    [Fact]
    public void RefitIgnoresSlotsItWasToldNotToTrust()
    {
        var truth = new Geometry(970.0, 120.0, 8.0, 1.70);
        var centers = truth.Centers().Select(c => (double?)c).ToList();
        var weights = Enumerable.Repeat(1.0, 12).ToList();
        foreach (var bad in new[] { 4, 7, 8, 11 })
        {
            centers[bad] += 60.0;
            weights[bad] = 0.0;
        }
        var refit = Layout.FitGeometry(centers, weights, truth);
        Assert.True(Math.Abs(refit.CenterX - 970.0) < 0.5);
        Assert.True(Math.Abs(refit.Pitch - 120.0) < 0.5);
    }

    // -- descriptor internals --------------------------------------------------

    [Fact]
    public void FastBlurMatchesTheReferenceBoxBlur()
    {
        var random = new Random(20240919);
        const int cells = ImageOps.GridW * ImageOps.GridH;
        var plane = Enumerable.Range(0, cells).Select(_ => (float)random.NextDouble() * 2f - 1f).ToArray();
        var blurred = new float[cells];
        ImageOps.BoxBlur(plane, blurred);

        // The reference: average every cell of the edge-clamped 9×9 window around each cell directly.
        const int r = ImageOps.HighpassRadius;
        for (var y = 0; y < ImageOps.GridH; y++)
        {
            for (var x = 0; x < ImageOps.GridW; x++)
            {
                var total = 0.0;
                for (var dy = -r; dy <= r; dy++)
                {
                    for (var dx = -r; dx <= r; dx++)
                        total += plane[Math.Clamp(y + dy, 0, ImageOps.GridH - 1) * ImageOps.GridW + Math.Clamp(x + dx, 0, ImageOps.GridW - 1)];
                }
                Assert.True(Math.Abs(total / ((2 * r + 1) * (2 * r + 1)) - blurred[y * ImageOps.GridW + x]) < 1e-4);
            }
        }
    }

    [Fact]
    public void ScoresCollapseVariantsToAHerosBest()
    {
        // Two heroes; the first has two variants, the second one.
        var bank = new TemplateBank(["a", "b"], [0, 0, 1], [[1f, 0f], [0f, 1f], [-1f, 0f]],
            [new("a", "a.png", TemplateKind.Api), new("a", "a/variant_01.png", TemplateKind.Learned), new("b", "b.png", TemplateKind.Api)]);
        var bestRows = new int[2];
        var scores = bank.Scores([0f, 1f], bestRows);
        Assert.Equal(1.0f, scores[0], 6);
        Assert.Equal(0.0f, scores[1], 6);
        Assert.Equal([1, 2], bestRows);
    }

    [Fact]
    public void AnAlternatesNameSaysWhereItCameFrom()
    {
        Assert.Equal(TemplateKind.Api, TemplateSource.Of("haze", "haze.png", isAlternate: false).Kind);
        Assert.Equal(TemplateKind.Bundled, TemplateSource.Of("seven", "seven/bundled_01.png", isAlternate: true).Kind);
        Assert.Equal(TemplateKind.Bundled, TemplateSource.Of("seven", "seven/ingame_01.png", isAlternate: true).Kind);
        Assert.Equal((TemplateKind.Derived, PortraitState.Normal), Describe("haze/card_normal.png"));
        Assert.Equal((TemplateKind.Derived, PortraitState.Critical), Describe("haze/state_critical.png"));
        Assert.Equal((TemplateKind.Derived, PortraitState.Gloat), Describe("haze/state_gloat.png"));
        Assert.Equal((TemplateKind.Learned, PortraitState.Normal), Describe("haze/variant_03.png"));
        Assert.Equal((TemplateKind.Learned, PortraitState.Normal), Describe("haze/on_fire_01.png"));

        static (TemplateKind, PortraitState) Describe(string path)
        {
            var source = TemplateSource.Of("haze", path, isAlternate: true);
            return (source.Kind, source.State);
        }
    }

    [Fact]
    public void ABankCanBeNarrowedToTheImagesItWasGiven()
    {
        var clean = Bank.Where(source => source.Kind == TemplateKind.Api);

        Assert.Equal(Bank.Heroes, clean.Heroes);
        Assert.Equal(Bank.Heroes.Count, clean.Vectors.Count);
        Assert.All(clean.Sources, source => Assert.Equal(TemplateKind.Api, source.Kind));
        Assert.Contains(Bank.Sources, source => source.Kind != TemplateKind.Api);

        var onlyHaze = Bank.Where(source => source.Hero == "haze");
        Assert.Equal(["haze"], onlyHaze.Heroes);
        Assert.All(onlyHaze.RowsHero, hero => Assert.Equal(0, hero));
    }

    [Fact]
    public void PngRoundTripsAndSavedVariantsAreNumbered()
    {
        using var temp = new TempDirectory();
        var crop = Image("fixtures/cropped_strip_1769.png").Crop(10, 5, 70, 105);

        var first = TemplateBank.SaveVariant(temp.Path, "haze", crop);
        var second = TemplateBank.SaveVariant(temp.Path, "haze", crop);

        Assert.Equal(Path.Combine(temp.Path, "haze", "variant_01.png"), first);
        Assert.Equal(Path.Combine(temp.Path, "haze", "variant_02.png"), second);
        var loaded = ImageFile.Load(first);
        Assert.Equal((crop.Width, crop.Height), (loaded.Width, loaded.Height));
        Assert.Equal(crop.Pixels, loaded.Pixels);
        Assert.Equal(2, TemplateBank.Load(temp.Path).Vectors.Count);
    }

    // -- end to end, against real captures -------------------------------------

    public static TheoryData<string> Fixtures() =>
        new(Directory.GetFiles(Golden.PathOf("vision", "fixtures"), "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void FixturesAreReadCorrectly(string name)
    {
        var spec = FixtureSpec(name);
        var result = DetectFixture(name);
        Assert.NotNull(result);

        if ((int?)spec["self_slot"] is { } expectedSelf)
            Assert.Equal(expectedSelf, result.SelfSlot);

        var wanted = spec["heroes"]?.AsObject().ToDictionary(pair => int.Parse(pair.Key), pair => (string)pair.Value!) ?? [];
        var wrong = wanted.Where(pair => result.HeroAt(pair.Key) != pair.Value).ToList();
        Assert.True(wrong.Count <= ((int?)spec["allow_wrong"] ?? 0),
            $"{name}: misread {string.Join(", ", wrong.Select(pair => $"{pair.Key}: {result.HeroAt(pair.Key)} (expected {pair.Value})"))}");
    }

    /// <summary>
    /// The grid is searched for, never measured, so a different resolution or HUD scale should cost
    /// nothing: the captures are rescaled and nudged copies of one fixture (made by the golden export,
    /// since they need Pillow's LANCZOS).
    /// </summary>
    [Theory]
    [InlineData("strip_0.80_0_0")]
    [InlineData("strip_1.00_4_3")]
    [InlineData("strip_1.35_0_0")]
    public void DetectionSurvivesRescalingAndDrift(string variant)
    {
        // Slot 1 is left out: Victor against Sinclair there is inside the matcher's own uncertain margin.
        var expected = new Dictionary<int, string?> { [0] = "abrams", [2] = "paige", [6] = "shiv", [9] = "haze", [10] = "bebop" };
        var result = Detector.Detect(Image($"variants/{variant}.png"), Bank, pitchRange: (70.0, 200.0), topRange: (0.0, 40.0));

        Assert.NotNull(result);
        Assert.Equal(expected, expected.ToDictionary(pair => pair.Key, pair => result.HeroAt(pair.Key)));
        Assert.All(expected.Keys, slot => Assert.True(result.Slots[slot].IsConfident, $"slot {slot} read correctly but not confidently"));
        Assert.Equal(2, result.SelfSlot);
    }

    /// <summary>A 2551-wide copy of a 2560 capture once picked a pitch of 101 against a true 117, shifting the roster a slot.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(17)]
    public void AFewPixelsOfWidthDoNotChangeTheAnswer(int trim)
    {
        var band = Image("fixtures/screen_2560x1440_band_2.png");
        var spec = JsonNode.Parse(File.ReadAllText(Golden.PathOf("vision", "fixtures", "screen_2560x1440_band_2.json")))!;
        var result = Detector.Detect(band.Crop(0, 0, band.Width - trim, band.Height), Bank, screenHeight: 1440);

        Assert.NotNull(result);
        foreach (var (slot, hero) in spec["heroes"]!.AsObject())
            Assert.Equal((string)hero!, result.HeroAt(int.Parse(slot)));
        Assert.Equal((int)spec["self_slot"]!, result.SelfSlot);
    }

    [Fact]
    public void SelfIsFoundWhenLaneMatesAreAlsoLit()
    {
        var (slot, value) = Detector.FindSelfSlot([4.7, 8.9, 0.5, 2.5, 0.5, 0.4, 6.6, 3.6, 0.4, 0.2, 0.4, 0.3]);
        Assert.Equal(1, slot);
        Assert.Equal(8.9, value);
    }

    [Fact]
    public void SelfIsRefusedWhenNothingStandsOut()
    {
        Assert.Null(Detector.FindSelfSlot(Enumerable.Repeat(1.0, 12).ToList()).Slot);
        Assert.Null(Detector.FindSelfSlot([.. Enumerable.Repeat(0.3, 11), 0.9]).Slot);
        Assert.Null(Detector.FindSelfSlot([]).Slot);
    }
}
