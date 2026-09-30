using System.Text.Json.Nodes;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Vision;
using static DeadlockAdvisor.Tests.Support.VisionData;

namespace DeadlockAdvisor.Tests;

/// <summary>Top-bar portraits cut from real API cards (Golden/vision/cards): Haze and Lash fit their cards, Apollo's top-bar art is stale.</summary>
public sealed class TopbarDerivationTests : IDisposable
{
    private static readonly string[] _heroes = ["apollo", "haze", "lash"];
    private readonly TempDirectory _topbar = new();

    public TopbarDerivationTests()
    {
        foreach (var hero in _heroes)
        {
            File.Copy(Path.Combine(TopbarDir, hero + ".png"), Path.Combine(_topbar.Path, hero + ".png"));
            foreach (var state in Enum.GetValues<PortraitState>())
                CopyCard(hero, state);
        }
    }

    public void Dispose() => _topbar.Dispose();

    private void CopyCard(string hero, PortraitState state)
    {
        var folder = TopbarDerivation.CardFolder(_topbar.Path, state);
        Directory.CreateDirectory(folder);
        File.Copy(Golden.PathOf("vision", "cards", state.ToString().ToLowerInvariant(), hero + ".png"), Path.Combine(folder, hero + ".png"), overwrite: true);
    }

    private string Output(string hero, PortraitState state) => Path.Combine(_topbar.Path, hero, TopbarDerivation.OutputName(state));

    private JsonObject State() => JsonNode.Parse(File.ReadAllText(Path.Combine(_topbar.Path, TopbarDerivation.StateFile)))!["heroes"]!.AsObject();

    [Fact]
    public void TopBarArtIsFoundInsideItsCard()
    {
        var registration = TopbarDerivation.Register(ImageFile.LoadWithAlpha(Path.Combine(TopbarDir, "haze.png")),
            ImageFile.LoadWithAlpha(Golden.PathOf("vision", "cards", "normal", "haze.png")));

        Assert.NotNull(registration);
        Assert.True(registration.IsAccepted);
        Assert.True(registration.Score >= 0.98, $"haze fits at {registration.Score}");
        Assert.Equal(0.215, registration.Frame.X, 0.01);
        Assert.Equal(0.186, registration.Frame.Y, 0.01);
        Assert.Equal(0.620, registration.Frame.W, 0.01);
    }

    [Fact]
    public void EveryStateIsCutAndStaleArtTakesTheMedianCrop()
    {
        var outcome = TopbarDerivation.Run(_topbar.Path, _heroes);

        Assert.Equal(_heroes, outcome.Derived);
        Assert.Equal(["apollo"], outcome.Fallbacks);
        Assert.Empty(outcome.Failed);
        foreach (var hero in _heroes)
        {
            foreach (var state in Enum.GetValues<PortraitState>())
            {
                var image = ImageFile.Load(Output(hero, state));
                Assert.Equal((120, 200), (image.Width, image.Height));
            }
        }
        var records = State();
        Assert.Equal("median", (string?)records["apollo"]!["source"]);
        Assert.Equal("registered", (string?)records["haze"]!["source"]);

        // The cut from Haze's own card is the top-bar art it was fitted to, near enough.
        var cut = ImageOps.Descriptor(ImageFile.Load(Output("haze", PortraitState.Normal)))!;
        var art = ImageOps.Descriptor(ImageFile.Load(Path.Combine(TopbarDir, "haze.png")))!;
        var similarity = cut.Zip(art).Sum(pair => pair.First * pair.Second);
        Assert.True(similarity > 0.8, $"cut vs art: {similarity}");
    }

    [Fact]
    public void OnlyWhatChangedIsCutAgain()
    {
        TopbarDerivation.Run(_topbar.Path, _heroes);
        Assert.Empty(TopbarDerivation.Run(_topbar.Path, _heroes).Derived);

        // A new card for Lash; Haze's gloat card gone.
        File.Copy(Golden.PathOf("vision", "cards", "critical", "haze.png"), Path.Combine(TopbarDerivation.CardFolder(_topbar.Path, PortraitState.Critical), "lash.png"),
            overwrite: true);
        File.Delete(Path.Combine(TopbarDerivation.CardFolder(_topbar.Path, PortraitState.Gloat), "haze.png"));
        var again = TopbarDerivation.Run(_topbar.Path, _heroes);

        Assert.Equal(["haze", "lash"], again.Derived);
        Assert.False(File.Exists(Output("haze", PortraitState.Gloat)));
        Assert.True(File.Exists(Output("haze", PortraitState.Critical)));
    }

    [Fact]
    public void UnreadableArtIsReportedNotThrown()
    {
        File.WriteAllBytes(Path.Combine(TopbarDerivation.CardFolder(_topbar.Path, PortraitState.Normal), "lash.png"), [1, 2, 3]);

        var outcome = TopbarDerivation.Run(_topbar.Path, _heroes);

        Assert.Single(outcome.Failed, line => line.StartsWith("lash:", StringComparison.Ordinal));
        Assert.Contains("haze", outcome.Derived);
    }
}
