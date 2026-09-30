using System.Text.Json.Nodes;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Tests;

/// <summary>Top-bar portraits cut from real API cards (Golden/vision/cards).</summary>
public sealed class TopbarDerivationTests : IDisposable
{
    private static readonly string[] _heroes = ["apollo", "haze", "lash"];
    private readonly TempDirectory _topbar = new();

    public TopbarDerivationTests()
    {
        foreach (var hero in _heroes)
        {
            foreach (var state in Enum.GetValues<PortraitState>())
                CopyCard(hero, state, hero);
        }
    }

    public void Dispose() => _topbar.Dispose();

    private void CopyCard(string hero, PortraitState state, string asHero)
    {
        var folder = TopbarDerivation.CardFolder(_topbar.Path, state);
        Directory.CreateDirectory(folder);
        File.Copy(Golden.PathOf("vision", "cards", state.ToString().ToLowerInvariant(), hero + ".png"), Path.Combine(folder, asHero + ".png"), overwrite: true);
    }

    private string Output(string hero, PortraitState state) => Path.Combine(_topbar.Path, hero, TopbarDerivation.OutputName(state));

    [Fact]
    public void EveryCardIsCutWhereTheTopBarCropsIt()
    {
        var outcome = TopbarDerivation.Run(_topbar.Path, _heroes);

        Assert.Equal(_heroes, outcome.Derived);
        Assert.Empty(outcome.Failed);
        foreach (var hero in _heroes)
        {
            foreach (var state in Enum.GetValues<PortraitState>())
            {
                var image = ImageFile.Load(Output(hero, state));
                Assert.Equal((120, 200), (image.Width, image.Height));
            }
        }
        // The cut is the crop, over the grey the see-through parts are laid on.
        var card = ImageFile.LoadWithAlpha(Golden.PathOf("vision", "cards", "normal", "haze.png"));
        Assert.Equal(TopbarDerivation.Cut(card, TopbarDerivation.InGameFrame).Pixels, ImageFile.Load(Output("haze", PortraitState.Normal)).Pixels);
        Assert.True(TopbarDerivation.IsCurrent(_topbar.Path));
    }

    [Fact]
    public void OnlyWhatChangedIsCutAgain()
    {
        TopbarDerivation.Run(_topbar.Path, _heroes);
        Assert.Empty(TopbarDerivation.Run(_topbar.Path, _heroes).Derived);

        // A new critical card for Lash; Haze's on-fire card gone.
        CopyCard("haze", PortraitState.Critical, "lash");
        File.Delete(Path.Combine(TopbarDerivation.CardFolder(_topbar.Path, PortraitState.Gloat), "haze.png"));
        var again = TopbarDerivation.Run(_topbar.Path, _heroes);

        Assert.Equal(["haze", "lash"], again.Derived);
        Assert.False(File.Exists(Output("haze", PortraitState.Gloat)));
        Assert.True(File.Exists(Output("haze", PortraitState.Critical)));
    }

    [Fact]
    public void PortraitsCutByAnOlderVersionAreCutAgain()
    {
        TopbarDerivation.Run(_topbar.Path, _heroes);
        var state = Path.Combine(_topbar.Path, TopbarDerivation.StateFile);
        var json = JsonNode.Parse(File.ReadAllText(state))!;
        json["version"] = TopbarDerivation.Version - 1;
        File.WriteAllText(state, json.ToJsonString());
        Assert.False(TopbarDerivation.IsCurrent(_topbar.Path));

        Assert.Equal(_heroes, TopbarDerivation.Run(_topbar.Path, _heroes).Derived);
        Assert.True(TopbarDerivation.IsCurrent(_topbar.Path));
    }

    [Fact]
    public void UnreadableCardsAreReportedNotThrown()
    {
        File.WriteAllBytes(Path.Combine(TopbarDerivation.CardFolder(_topbar.Path, PortraitState.Normal), "lash.png"), [1, 2, 3]);

        var outcome = TopbarDerivation.Run(_topbar.Path, _heroes);

        Assert.Single(outcome.Failed, line => line.StartsWith("lash:", StringComparison.Ordinal));
        Assert.Equal(["apollo", "haze"], outcome.Derived);
    }
}
