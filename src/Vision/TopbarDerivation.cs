using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeadlockAdvisor.Vision;

/// <summary>
/// Top-bar portraits cut from the API's hero cards. The top bar shows every hero's card cropped the
/// same way (<see cref="InGameFrame"/>), in whichever state the hero is in: normal, on critical
/// health, or on a kill streak. The cards are current and come in all three states, whereas the
/// API's own top-bar art is cropped hero by hero, is stale for some heroes, and has no critical or
/// on-fire versions at all. What's cut from what is kept in topbar/_derived.json, so a hero is only
/// cut again when one of their cards changes.
/// </summary>
public static class TopbarDerivation
{
    /// <summary>Bumped whenever a change here makes portraits cut before it wrong.</summary>
    public const int Version = 2;

    public const string StateFile = "_derived.json";

    /// <summary>
    /// Where the top bar crops a hero's card, measured off labelled captures of 30 heroes (every one
    /// within about a hundredth of it). Its box on screen is the grid's slot box, at
    /// <see cref="WidthRatio"/> pitches wide, so every portrait cut here sits in exactly the same place.
    /// </summary>
    public static readonly Frame InGameFrame = new(0.164, 0.148, 0.661);

    /// <summary>How wide, in pitches, a slot's portrait is drawn: the width the cut portraits are matched at.</summary>
    public const double WidthRatio = 0.526;

    /// <summary>What the see-through parts of a card are laid over. The game shows a team colour there; grey is between them.</summary>
    public static readonly (byte R, byte G, byte B) Background = (110, 110, 110);

    private const int ArtWidth = 120;
    private const int ArtHeight = 200;

    private static readonly string[] _imageSuffixes = [".png", ".webp", ".jpg", ".jpeg", ".bmp"];

    /// <summary>
    /// A crop of a card: its left, top and width as fractions of the card's width, height and
    /// width. Its height follows from the top-bar art's shape, so it fits cards of any size.
    /// </summary>
    public readonly record struct Frame(double X, double Y, double W);

    /// <param name="Derived">Heroes whose portraits were cut this time.</param>
    /// <param name="Failed">"haze: ...": heroes whose cards couldn't be read.</param>
    public sealed record Outcome(IReadOnlyList<string> Derived, IReadOnlyList<string> Failed);

    /// <summary>Where a hero's card for one portrait state is kept, under the top-bar folder but out of the template bank's sight.</summary>
    public static string CardFolder(string topbarDir, PortraitState state) =>
        Path.Combine(topbarDir, "_cards", state.ToString().ToLowerInvariant());

    /// <summary>The file a portrait cut from the card for <paramref name="state"/> is saved as, in the hero's folder.</summary>
    public static string OutputName(PortraitState state) => state switch
    {
        PortraitState.Normal => "card_normal.png",
        PortraitState.Critical => "state_critical.png",
        _ => "state_gloat.png",
    };

    /// <summary>Whether the portraits in <paramref name="topbarDir"/> were cut by this version of the derivation, if any were.</summary>
    public static bool IsCurrent(string topbarDir) => LoadState(topbarDir) is { Version: Version };

    /// <summary>
    /// Cut the portraits of every hero whose cards changed since last time (all of them when
    /// <paramref name="force"/>, or when an older version cut them), and remove any whose card has gone.
    /// </summary>
    public static Outcome Run(string topbarDir, IEnumerable<string> heroes, bool force = false)
    {
        var previous = LoadState(topbarDir);
        var fresh = force || previous?.Version != Version;
        var inputs = new SortedDictionary<string, string?[]>(StringComparer.Ordinal);
        var derived = new List<string>();
        var failed = new List<string>();
        foreach (var hero in heroes.Order(StringComparer.Ordinal))
        {
            var cards = Enum.GetValues<PortraitState>().Select(state => FindImage(CardFolder(topbarDir, state), hero)).ToArray();
            string?[] hashes;
            try
            {
                hashes = cards.Select(card => card is null ? null : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(card)))).ToArray();
                if (!fresh && previous!.Heroes.TryGetValue(hero, out var known) && known.SequenceEqual(hashes))
                {
                    inputs[hero] = hashes;
                    continue;
                }
                WriteOutputs(topbarDir, hero, cards);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                failed.Add($"{hero}: {ex.Message}");
                continue;
            }
            inputs[hero] = hashes;
            if (cards.Any(card => card is not null))
                derived.Add(hero);
        }
        if (derived.Count > 0)
            TemplateBank.InvalidatePythonCache(topbarDir);
        SaveState(topbarDir, inputs);
        return new Outcome(derived, failed);
    }

    /// <summary>A card cut where the top bar crops it, over <see cref="Background"/>, at the top-bar art's size.</summary>
    public static RgbImage Cut((RgbImage Image, byte[] Alpha) card, Frame frame)
    {
        var laid = ImageOps.Composite(card.Image, card.Alpha, Background);
        var width = frame.W * laid.Width;
        var height = width * Layout.ArtAspect;
        var left = Math.Clamp((int)Math.Round(frame.X * laid.Width), 0, laid.Width - 1);
        var top = Math.Clamp((int)Math.Round(frame.Y * laid.Height), 0, laid.Height - 1);
        var right = Math.Clamp((int)Math.Round(frame.X * laid.Width + width), left + 1, laid.Width);
        var bottom = Math.Clamp((int)Math.Round(frame.Y * laid.Height + height), top + 1, laid.Height);
        return ImageOps.ResizeRgb(laid.Crop(left, top, right, bottom), ArtWidth, ArtHeight);
    }

    /// <param name="cards">Each state's card, in <see cref="PortraitState"/> order, or null where there's none.</param>
    private static void WriteOutputs(string topbarDir, string hero, string?[] cards)
    {
        var folder = Path.Combine(topbarDir, hero);
        foreach (var state in Enum.GetValues<PortraitState>())
        {
            var output = Path.Combine(folder, OutputName(state));
            if (cards[(int)state] is not { } card)
            {
                if (File.Exists(output))
                    File.Delete(output);
                continue;
            }
            var cut = Cut(ImageFile.LoadWithAlpha(card), InGameFrame);
            Directory.CreateDirectory(folder);
            Png.Save(cut, output);
        }
    }

    private static string? FindImage(string folder, string stem) =>
        _imageSuffixes.Select(suffix => Path.Combine(folder, stem + suffix)).FirstOrDefault(File.Exists);

    // -- what was cut from what ---------------------------------------------------------------

    /// <param name="Heroes">Each hero's card hashes, in <see cref="PortraitState"/> order, null where there's no card.</param>
    private sealed record State(int Version, Dictionary<string, string?[]> Heroes);

    private static State? LoadState(string topbarDir)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(topbarDir, StateFile))) is not JsonObject json)
                return null;
            var heroes = json["heroes"]!.AsObject()
                .ToDictionary(pair => pair.Key, pair => pair.Value!.AsArray().Select(value => (string?)value).ToArray(), StringComparer.Ordinal);
            return new State((int)json["version"]!, heroes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
                                       or NullReferenceException or FormatException)
        {
            return null;
        }
    }

    private static void SaveState(string topbarDir, IReadOnlyDictionary<string, string?[]> heroes)
    {
        var json = new JsonObject
        {
            ["version"] = Version,
            ["heroes"] = new JsonObject(heroes.Select(pair =>
                KeyValuePair.Create(pair.Key, (JsonNode?)new JsonArray(pair.Value.Select(hash => (JsonNode?)hash).ToArray())))),
        };
        Directory.CreateDirectory(topbarDir);
        File.WriteAllText(Path.Combine(topbarDir, StateFile), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
