using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Features.Match.Detect;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Tests.Ui;
using DeadlockAdvisor.Vision;
using SkiaSharp;
using static DeadlockAdvisor.Tests.Support.VisionData;

namespace DeadlockAdvisor.Tests;

/// <summary>
/// Turning captures into labelled fixtures. Not tests: each does nothing unless its environment
/// variable points it at a folder.
/// </summary>
public class VisionCorpusTools
{
    public static string DraftsDir => Path.Combine(UiHarness.RepoRoot(), "mockups", "vision_drafts");

    private static readonly JsonSerializerOptions _indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Screen heights a band's height could have come from, commonest first.
    private static readonly int[] _screenHeights = [1080, 1440, 2160, 1200, 1600, 900, 768, 720, 1050, 1024];

    /// <summary>
    /// DEADLOCK_VISION_DRAFT=&lt;captures folder&gt;: for every distinct capture there, a draft label
    /// (the detector's own reading) and a contact sheet of each slot beside its best candidates, in
    /// mockups/vision_drafts, to be checked by eye before the capture becomes a fixture.
    /// </summary>
    [Fact]
    public void DraftLabelsForCaptures()
    {
        if (Environment.GetEnvironmentVariable("DEADLOCK_VISION_DRAFT") is not { Length: > 0 } folder)
            return;
        Directory.CreateDirectory(DraftsDir);
        var seen = new HashSet<string>();
        foreach (var path in Directory.GetFiles(folder, "*.png").Order(StringComparer.Ordinal))
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!seen.Add(hash))
                continue;
            var band = ImageFile.Load(path);
            var screenHeight = ScreenHeightOf(band.Height);
            var detection = Detector.Detect(band, Bank, GridBeside(path), screenHeight: screenHeight)
                            ?? Detector.Detect(band, Bank, screenHeight: screenHeight);
            if (detection is null)
                continue;

            var name = Path.GetFileNameWithoutExtension(path);
            var draft = LabeledCapture.FromApplied(detection, detection.Slots.Select(slot => slot.HeroId).ToList(), detection.SelfSlot ?? -1,
                [], reviewed: false, band.Width, screenHeight);
            var json = draft.ToJson();
            json["source"] = Path.GetFileName(path);
            json["sha256"] = hash;
            File.WriteAllText(Path.Combine(DraftsDir, name + ".json"), json.ToJsonString(_indented) + "\n");
            SaveContactSheet(detection, Path.Combine(DraftsDir, name + ".png"));
        }
    }

    /// <summary>
    /// DEADLOCK_VISION_REDACT=1: blacks out the "redacted" rectangles ([x, y, width, height]) each
    /// fixture and variant lists in its .json: a video's title, a streamer's overlay, anything in a
    /// frame that isn't the game. The rectangles stay clear of the portraits and pills, so every reading
    /// comes out the same; <see cref="VisionTests.RedactedRegionsStayBlank"/> keeps them black.
    /// </summary>
    [Fact]
    public void RedactFixtures()
    {
        if (Environment.GetEnvironmentVariable("DEADLOCK_VISION_REDACT") != "1")
            return;
        foreach (var (image, regions) in VisionTests.RedactedRegions(source: true))
        {
            var pixels = ImageFile.Load(image);
            foreach (var (x, y, width, height) in regions)
            {
                for (var row = y; row < Math.Min(y + height, pixels.Height); row++)
                    Array.Clear(pixels.Pixels, (row * pixels.Width + x) * 3, (Math.Min(x + width, pixels.Width) - x) * 3);
            }
            Png.Save(pixels, image);
        }
    }

    /// <summary>
    /// DEADLOCK_VISION_PROMOTE=&lt;captures folder&gt;: every label in Golden/vision/corpus without its
    /// image yet gets the capture it names as its "source", cut down to the strip: 1920 wide about the
    /// grid's centre and the top 180 rows, which keeps the portraits and pills and drops most of the
    /// names, chat and HUD corners. The grid is shifted to match and saved with the label, so reading
    /// a corpus capture needs no search.
    /// </summary>
    [Fact]
    public void PromoteLabelledCaptures()
    {
        const int width = 1920, height = 180;
        if (Environment.GetEnvironmentVariable("DEADLOCK_VISION_PROMOTE") is not { Length: > 0 } folder)
            return;
        foreach (var labels in Directory.GetFiles(Golden.SourcePathOf("vision", "corpus"), "*.json"))
        {
            var image = Path.ChangeExtension(labels, ".png");
            if (File.Exists(image))
                continue;
            var json = JsonNode.Parse(File.ReadAllText(labels))!.AsObject();
            var source = Path.Combine(folder, (string)json["source"]!);
            var band = ImageFile.Load(source);
            var grid = GridBeside(source)
                       ?? Detector.Detect(band, Bank, screenHeight: (int?)json["screen_height"])?.Geometry
                       ?? throw new InvalidOperationException($"No strip found in {source}");
            var cropWidth = Math.Min(width, band.Width);
            var left = Math.Clamp((int)Math.Round(grid.CenterX - cropWidth / 2.0), 0, band.Width - cropWidth);
            Png.Save(band.Crop(left, 0, left + cropWidth, Math.Min(height, band.Height)), image);
            json["grid"] = (grid with { CenterX = grid.CenterX - left }).ToJson();
            File.WriteAllText(labels, json.ToJsonString(_indented) + "\n");
        }
    }

    /// <summary>
    /// DEADLOCK_VISION_FETCH_ART=&lt;folder&gt;: the real art download, from deadlock-api.com, into that
    /// folder, for the heroes in the golden data: the cards the test bank's derived portraits are cut
    /// from, and whatever the API's art looks like today.
    /// </summary>
    [Fact]
    public async Task FetchArt()
    {
        if (Environment.GetEnvironmentVariable("DEADLOCK_VISION_FETCH_ART") is not { Length: > 0 } folder)
            return;
        using var api = new DeadlockApi();
        var download = new ArtDownloadService(new GameApiService(api), api);
        await download.DownloadAsync(Golden.LoadStore(), folder, force: false, null, CancellationToken.None);
    }

    /// <summary>
    /// DEADLOCK_VISION_DERIVE=&lt;assets folder&gt; (one <see cref="FetchArt"/> filled): cut every hero's
    /// portraits from their cards, and draw mockups/vision_drafts/derived.png, each hero's API top-bar
    /// art beside the portraits cut from their normal, critical and on-fire cards.
    /// </summary>
    [Fact]
    public void DeriveArt()
    {
        if (Environment.GetEnvironmentVariable("DEADLOCK_VISION_DERIVE") is not { Length: > 0 } assets)
            return;
        var topbar = Path.Combine(assets, "topbar");
        var heroes = Golden.LoadStore().Heroes.Keys.Order(StringComparer.Ordinal).ToList();
        TopbarDerivation.Run(topbar, heroes, force: true);

        const int cellW = 120, cellH = 200, caption = 18, perRow = 4;
        var rows = (heroes.Count + perRow - 1) / perRow;
        var info = new SKImageInfo(perRow * (4 * cellW + 16), rows * (cellH + caption));
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(24, 24, 28));
        using var font = new SKFont(SKTypeface.Default, 13);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        for (var i = 0; i < heroes.Count; i++)
        {
            var hero = heroes[i];
            var (x, y) = (i % perRow * (4 * cellW + 16), i / perRow * (cellH + caption));
            string?[] images =
            [
                Path.Combine(topbar, hero + ".png"),
                .. Enum.GetValues<PortraitState>().Select(s => Path.Combine(topbar, hero, TopbarDerivation.OutputName(s))),
            ];
            for (var column = 0; column < images.Length; column++)
            {
                if (images[column] is { } image && File.Exists(image))
                    DrawImage(canvas, ImageFile.Load(image), x + column * cellW, y, cellW, cellH);
            }
            canvas.DrawText(hero, x + 2, y + cellH + 14, font, text);
        }
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        Directory.CreateDirectory(DraftsDir);
        File.WriteAllBytes(Path.Combine(DraftsDir, "derived.png"), data.ToArray());
    }

    private static void DrawImage(SKCanvas canvas, RgbImage image, int x, int y, int width, int height)
    {
        var scaled = image.Resize(width, height, ResampleFilter.Bilinear);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 4] = scaled.Pixels[i * 3];
            pixels[i * 4 + 1] = scaled.Pixels[i * 3 + 1];
            pixels[i * 4 + 2] = scaled.Pixels[i * 3 + 2];
            pixels[i * 4 + 3] = 255;
        }
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        canvas.DrawBitmap(bitmap, x, y);
    }

    /// <summary>
    /// DEADLOCK_VISION_AUDIT=&lt;topbar folder&gt;: every learned portrait there (a correction saved from
    /// the review) added to the test bank on its own, and every labelled capture read again. One that
    /// breaks a read, or rescues none, is listed for the quarantine in mockups/variant_audit.md; with
    /// DEADLOCK_VISION_AUDIT_APPLY=1 it's moved there too.
    /// </summary>
    [Fact]
    public void AuditLearnedPortraits()
    {
        if (Environment.GetEnvironmentVariable("DEADLOCK_VISION_AUDIT") is not { Length: > 0 } topbar)
            return;
        var apply = Environment.GetEnvironmentVariable("DEADLOCK_VISION_AUDIT_APPLY") == "1";
        var captures = VisionCorpusTests.Labelled();
        var baseline = VisionEval.Run(captures, Bank).SelectMany(outcome => outcome.Slots).ToList();
        var learned = Directory.GetDirectories(topbar)
            .Where(folder => !Path.GetFileName(folder).StartsWith('_'))
            .SelectMany(folder => Directory.GetFiles(folder).Select(file => TemplateSource.Of(Path.GetFileName(folder), file, isAlternate: true)))
            .Where(source => source.Kind == TemplateKind.Learned && ImageFile.Suffixes.Contains(Path.GetExtension(source.Path).ToLowerInvariant()))
            .OrderBy(source => source.Path, StringComparer.Ordinal)
            .ToList();

        var report = new System.Text.StringBuilder("# Learned portraits\n\n| image | rescues | breaks | verdict |\n|---|---|---|---|\n");
        foreach (var source in learned)
        {
            var hero = Bank.Heroes.ToList().IndexOf(source.Hero);
            var vector = hero < 0 ? null : ImageOps.Descriptor(ImageFile.Load(source.Path));
            if (vector is null)
            {
                report.AppendLine($"| {Relative(source.Path)} | – | – | unreadable or unknown hero: quarantine |");
                Retire(source);
                continue;
            }
            var bank = new TemplateBank(Bank.Heroes, [.. Bank.RowsHero, hero], [.. Bank.Vectors, vector], [.. Bank.Sources, source]);
            var slots = VisionEval.Run(captures, bank).SelectMany(outcome => outcome.Slots).ToList();
            var pairs = baseline.Zip(slots).ToList();
            var rescues = pairs.Count(pair => !pair.First.Correct && pair.Second.Correct);
            var breaks = pairs.Count(pair => pair.First.Correct && !pair.Second.Correct
                                             || pair.First is { Correct: true, Confident: true } && !pair.Second.Confident
                                             || !pair.First.ConfidentWrong && pair.Second.ConfidentWrong);
            var keep = rescues > 0 && breaks == 0;
            report.AppendLine($"| {Relative(source.Path)} | {rescues} | {breaks} | {(keep ? "keep" : "quarantine")} |");
            if (!keep)
                Retire(source);
        }
        File.WriteAllText(Path.Combine(DraftsDir, "..", "variant_audit.md"), report.ToString());

        string Relative(string path) => Path.GetRelativePath(topbar, path).Replace('\\', '/');

        void Retire(TemplateSource source)
        {
            if (apply)
                TemplateBank.Quarantine(topbar, source.Hero, source.Path);
        }
    }

    /// <summary>The grid a capture was read with, from the note beside it.</summary>
    private static Geometry? GridBeside(string capture)
    {
        var json = Path.ChangeExtension(capture, ".json");
        if (File.Exists(json))
            return LabeledCapture.FromJson(JsonNode.Parse(File.ReadAllText(json))!).Grid;
        var note = Path.ChangeExtension(capture, ".txt");
        if (!File.Exists(note))
            return null;
        var line = File.ReadLines(note).FirstOrDefault(text => text.StartsWith("grid: ", StringComparison.Ordinal));
        return line is not null && JsonNode.Parse(line["grid: ".Length..]) is JsonObject grid ? Geometry.Parse(grid) : null;
    }

    /// <summary>The band is the top <see cref="ScreenCaptureService.BandFraction"/> of the screen, cut down to whole pixels.</summary>
    public static int ScreenHeightOf(int bandHeight) =>
        _screenHeights.Cast<int?>().FirstOrDefault(height => (int)(height!.Value * ScreenCaptureService.BandFraction) == bandHeight)
        ?? (int)Math.Round(bandHeight / ScreenCaptureService.BandFraction);

    /// <summary>A column per slot: the crop, then its three best heroes' art, each captioned.</summary>
    private static void SaveContactSheet(Detection detection, string path)
    {
        const int cellW = 120, cellH = 200, caption = 18, rows = 4;
        var info = new SKImageInfo(Layout.SlotCount * (cellW + 4), rows * (cellH + caption));
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(24, 24, 28));
        using var font = new SKFont(SKTypeface.Default, 13);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var weak = new SKPaint { Color = new SKColor(255, 170, 80), IsAntialias = true };

        for (var slot = 0; slot < Layout.SlotCount; slot++)
        {
            var x = slot * (cellW + 4);
            var reading = detection.Slots[slot];
            Draw(detection.CropOf(slot), x, 0);
            var self = detection.SelfSlot == slot ? " YOU" : "";
            canvas.DrawText($"{slot}: {reading.HeroId ?? "—"}{self}", x + 2, cellH + 14, font, reading.IsConfident ? text : weak);
            for (var rank = 0; rank < Math.Min(rows - 1, reading.Ranked.Count); rank++)
            {
                var (hero, score) = reading.Ranked[rank];
                var y = (rank + 1) * (cellH + caption);
                Draw(ImageFile.Load(Path.Combine(TopbarDir, hero + ".png")), x, y);
                canvas.DrawText(FormattableString.Invariant($"{hero} {score:0.00}"), x + 2, y + cellH + 14, font, text);
            }
        }

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());

        void Draw(RgbImage? image, int x, int y)
        {
            if (image is null)
                return;
            var scaled = image.Resize(cellW, cellH, ResampleFilter.Bilinear);
            using var bitmap = new SKBitmap(new SKImageInfo(cellW, cellH, SKColorType.Rgba8888, SKAlphaType.Opaque));
            var pixels = new byte[cellW * cellH * 4];
            for (var i = 0; i < cellW * cellH; i++)
            {
                pixels[i * 4] = scaled.Pixels[i * 3];
                pixels[i * 4 + 1] = scaled.Pixels[i * 3 + 1];
                pixels[i * 4 + 2] = scaled.Pixels[i * 3 + 2];
                pixels[i * 4 + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
            canvas.DrawBitmap(bitmap, x, y);
        }
    }
}
