using System.Text.Json.Nodes;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Tests.Ui;
using DeadlockAdvisor.Vision;
using static DeadlockAdvisor.Tests.Support.VisionData;

namespace DeadlockAdvisor.Tests;

public class NetWorthReaderTests
{
    public static readonly string[] Labelled =
        ["screen_2560x1440_band", "screen_2560x1440_band_2", "laning_2560x1440_band", "cropped_strip_1769", "starting_souls_2560x1440_band",
         "souls_under_1k_2560x1440_band", "two_k_2560x1440_band",
         "eight_min_2560x1440_band", "live_2560x1440_band"];

    /// <summary>The fixture's labels, as the game prints them: twelve pills left to right, then the two totals.</summary>
    private static (string[] Pills, string[] Totals) Labels(string fixture)
    {
        var spec = FixtureSpec(fixture);
        return (spec["net_worth"]!.AsArray().Select(value => (string)value!).ToArray(),
            spec["team_totals"]!.AsArray().Select(value => (string)value!).ToArray());
    }

    /// <summary>
    /// The grid saved beside an image, so reading net worth is tested apart from finding the strip,
    /// and a change to hero detection can't move the pills out from under these tests.
    /// </summary>
    private static Geometry GridOf(string image) =>
        Geometry.Parse(Golden.Json("vision/" + Path.ChangeExtension(image, ".json"))["grid"]!.AsObject())!;

    private static NetWorthReading Read(string image) => NetWorthReader.Read(Image(image), GridOf(image), NetWorthGlyphs.Bundled);

    /// <summary>What the labels come to in souls: the pills, then the totals.</summary>
    private static (int?[] Pills, int?[] Totals) Expected(string fixture)
    {
        var (pills, totals) = Labels(fixture);
        var totalSouls = totals.Select(NetWorthReader.TotalSouls).ToArray();
        return (pills.Select((printed, slot) => NetWorthReader.PillSouls(printed, totalSouls[slot / Layout.PerTeam])).ToArray(), totalSouls);
    }

    public static TheoryData<string> Fixtures() => new(Labelled);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryPillAndTotalIsReadAndAddsUp(string fixture)
    {
        var (pills, totals) = Expected(fixture);
        var reading = Read($"fixtures/{fixture}.png");

        Assert.Equal(pills, reading.Pills);
        Assert.Equal(totals, reading.Totals);
        Assert.True(reading.Agrees(0) && reading.Agrees(1));
        Assert.Equal(12, reading.ReadCount);
    }

    /// <summary>The rescaled and nudged copies stand in for other resolutions: 0.8 is about 1920×1080's scale.</summary>
    [Theory]
    [InlineData("variants/strip_0.80_0_0.png", "cropped_strip_1769")]
    [InlineData("variants/strip_1.00_4_3.png", "cropped_strip_1769")]
    [InlineData("variants/strip_1.35_0_0.png", "cropped_strip_1769")]
    [InlineData("variants/band_2_trim_9.png", "screen_2560x1440_band_2")]
    [InlineData("variants/band_2_trim_17.png", "screen_2560x1440_band_2")]
    public void OtherScalesAndOffsetsReadTheSame(string image, string fixture)
    {
        var (pills, totals) = Expected(fixture);
        var reading = Read(image);

        Assert.Equal(pills, reading.Souls);
        Assert.Equal(totals, reading.Totals);
    }

    [Fact]
    public void ASideWhosePillsDoNotAddUpIsDropped()
    {
        var reading = Read("fixtures/screen_2560x1440_band.png");
        var misread = reading with { Pills = reading.Pills.Select((souls, slot) => slot == 7 ? 35_000 : souls).ToList() };

        Assert.True(misread.Agrees(0));
        Assert.False(misread.Agrees(1));
        Assert.Equal(reading.Souls.Take(6), misread.Souls.Take(6));
        Assert.All(misread.Souls.Skip(6), souls => Assert.Null(souls));
    }

    [Theory]
    // Six pills at 1.2k are 6.9k to 7.5k before rounding, and a total of 7k means 7k to just under 8k.
    [InlineData(1_200, 7_000, true)]
    [InlineData(1_200, 6_000, true)]
    [InlineData(1_200, 8_000, false)]
    // Six at 20k are 117k to 123k.
    [InlineData(20_000, 123_000, true)]
    [InlineData(20_000, 117_000, true)]
    [InlineData(20_000, 124_000, false)]
    [InlineData(20_000, 116_000, false)]
    // Six at 600 are exactly 3,600, which the game totals as 3k.
    [InlineData(600, 3_000, true)]
    [InlineData(600, 4_000, false)]
    public void PillsAddUpToTheTotalWithinTheGamesRounding(int each, int total, bool agrees)
    {
        var reading = new NetWorthReading(Enumerable.Repeat<int?>(each, 12).ToList(), [total, total]);
        Assert.Equal(agrees, reading.Agrees(0));
    }

    [Fact]
    public void ASideWithAnUnreadPillOrTotalCannotAgree()
    {
        var pills = Enumerable.Repeat<int?>(20_000, 12).ToList();
        Assert.False(new NetWorthReading(pills, [null, 120_000]).Agrees(0));
        Assert.False(new NetWorthReading(pills, [null, 120_000]).Plausible(0));
        pills[7] = null;
        Assert.False(new NetWorthReading(pills, [120_000, 120_000]).Agrees(1));
    }

    /// <summary>
    /// A pill hidden under an overlay leaves the rest of its side read, as long as those read come to
    /// no more than the total: 846 843 820 820 824 and a hidden one, against 4k.
    /// </summary>
    [Fact]
    public void ASideWithHiddenPillsKeepsThoseReadWhileTheyFitTheTotal()
    {
        int?[] enemies = [846, null, 820, 820, 824, null];
        var reading = new NetWorthReading([.. Enumerable.Repeat<int?>(700, 6), .. enemies], [4_000, 4_000]);

        Assert.False(reading.Agrees(1));
        Assert.True(reading.Plausible(1));
        Assert.Equal(enemies, reading.Souls.Skip(6));

        var overTotal = reading with { Pills = [.. reading.Pills.Take(6), 8_400, .. enemies.Skip(1)] };
        Assert.False(overTotal.Plausible(1));
        Assert.All(overTotal.Souls.Skip(6), souls => Assert.Null(souls));
    }

    [Theory]
    [InlineData("600", 3_000, 600)]
    [InlineData("0", 0, 0)]
    [InlineData("1.2", 7_000, 1_200)]
    [InlineData("16", 90_000, 16_000)]
    [InlineData("112", 540_000, 112_000)]
    [InlineData("1.25", 7_000, null)]
    [InlineData(".5", 7_000, null)]
    [InlineData("", 7_000, null)]
    public void PillsArePrintedLikeTheGameDoes(string printed, int teamTotal, int? souls) =>
        Assert.Equal(souls, NetWorthReader.PillSouls(printed, teamTotal));

    [Theory]
    [InlineData("3", 3_000)]
    [InlineData("327", 327_000)]
    [InlineData("1.2", null)]
    [InlineData("", null)]
    public void TotalsAreWholeThousands(string printed, int? souls) => Assert.Equal(souls, NetWorthReader.TotalSouls(printed));

    [Fact]
    public void NoGridMatchesNothing()
    {
        var reading = NetWorthReader.Read(new RgbImage(400, 100), new Geometry(200, 30, 0), NetWorthGlyphs.Bundled);
        Assert.Equal(0, reading.ReadCount);
    }

    /// <summary>
    /// Each capture read with reference digits cut only from the others, as a capture from a new
    /// match would be: it may leave pills unread, but nothing it reports may be wrong.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ACaptureTheDigitsWereNotCutFromReadsNothingWrong(string fixture)
    {
        var glyphs = new NetWorthGlyphs(ReferenceFrames(Labelled.Where(other => other != fixture)));
        var image = $"fixtures/{fixture}.png";
        var reading = NetWorthReader.Read(Image(image), GridOf(image), glyphs);

        var (pills, _) = Expected(fixture);
        var wrong = reading.Souls.Select((souls, slot) => (Souls: souls, Slot: slot))
            .Where(pair => pair.Souls is not null && pair.Souls != pills[pair.Slot])
            .Select(pair => $"slot {pair.Slot}: {pair.Souls} for {pills[pair.Slot]}")
            .ToList();
        Assert.True(wrong.Count == 0, $"{fixture} ({reading.ReadCount} of 12 read): {string.Join(", ", wrong)}");
    }

    /// <summary>
    /// Every digit of the given fixtures' pills and totals, paired with its label. A pill or total
    /// whose glyphs don't line up one to one with its label's digits is left out rather than guessed at.
    /// </summary>
    private static List<(char Digit, float[] Frame)> ReferenceFrames(IEnumerable<string> fixtures)
    {
        var frames = new List<(char Digit, float[] Frame)>();
        foreach (var fixture in fixtures)
        {
            var (pills, totals) = Labels(fixture);
            var image = $"fixtures/{fixture}.png";
            var found = NetWorthReader.DigitFrames(Image(image), GridOf(image));
            foreach (var (glyphs, label) in found.Zip(pills.Concat(totals)))
            {
                var digits = label.Where(char.IsDigit).ToList();
                if (glyphs.Count == digits.Count)
                    frames.AddRange(digits.Zip(glyphs));
            }
        }
        return frames;
    }

    /// <summary>
    /// Rebuilds the embedded reference digits from the labelled fixtures (DEADLOCK_UPDATE_GOLDENS=1),
    /// and draws them into mockups/net_worth_glyphs.png to look over.
    /// </summary>
    [Fact]
    public void ReferenceDigitsAreBuiltFromTheLabelledFixtures()
    {
        var frames = ReferenceFrames(Labelled);

        // Every digit needs a reference, and nearly every glyph should have lined up with its label.
        Assert.Equal("0123456789", string.Concat(frames.Select(frame => frame.Digit).Distinct().Order()));
        var labelledDigits = Labelled.Select(Labels).Sum(labels => labels.Pills.Concat(labels.Totals).Sum(label => label.Count(char.IsDigit)));
        Assert.True(frames.Count >= labelledDigits * 0.95, $"only {frames.Count} of {labelledDigits} digits lined up with their labels");

        if (!Golden.Updating)
            return;
        var path = Path.Combine(UiHarness.RepoRoot(), "src", "Vision", "net_worth_glyphs.json");
        File.WriteAllText(path, NetWorthGlyphs.ToJson(frames).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        SaveContactSheet(frames, Path.Combine(UiHarness.RepoRoot(), "mockups", "net_worth_glyphs.png"));
    }

    private static void SaveContactSheet(List<(char Digit, float[] Frame)> frames, string path)
    {
        const int scale = 4;
        var groups = frames.GroupBy(frame => frame.Digit).OrderBy(group => group.Key).ToList();
        var columns = groups.Max(group => group.Count());
        var (cellWidth, cellHeight) = ((NetWorthReader.FrameWidth + 1) * scale, (NetWorthReader.FrameHeight + 1) * scale);
        var sheet = new RgbImage(columns * cellWidth, groups.Count * cellHeight);
        for (var row = 0; row < groups.Count; row++)
        {
            var column = 0;
            foreach (var (_, frame) in groups[row])
            {
                for (var y = 0; y < NetWorthReader.FrameHeight * scale; y++)
                {
                    for (var x = 0; x < NetWorthReader.FrameWidth * scale; x++)
                    {
                        var value = (byte)Math.Round(Math.Clamp(frame[y / scale * NetWorthReader.FrameWidth + x / scale], 0, 1) * 255);
                        var at = ((row * cellHeight + y) * sheet.Width + column * cellWidth + x) * 3;
                        sheet.Pixels[at] = sheet.Pixels[at + 1] = sheet.Pixels[at + 2] = value;
                    }
                }
                column++;
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Png.Save(sheet, path);
    }
}
