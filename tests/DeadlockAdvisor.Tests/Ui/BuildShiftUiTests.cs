using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match;
using DeadlockAdvisor.Features.Match.Results;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Tests.Ui;

/// <summary>What a hero has built moves their bullet and spirit damage in the list and the explanation, under a filter of its own.</summary>
public class BuildShiftUiTests
{
    private const string Hero = "haze";

    /// <summary>Every hero buys every shop item in the same numbers, so everyone's usual build is the shop's own.</summary>
    private static void GiveCounts(DataStore store)
    {
        var bought = store.Items.Values.Where(item => item.GameId != 0).ToDictionary(item => item.GameId, _ => new WinTotals(50, 100));
        var heroes = store.Heroes.Keys.ToDictionary(id => id, _ => new Halves(new Dictionary<long, WinTotals>(bought), []));
        var patch = new Patch("09-29-2026", 1790726400);
        store.MatchSegments =
        [
            new MatchSegment(patch, patch.Start, patch.Start + 86400, true, patch.Start + 3 * 86400,
                new SliceCounts(Halves.Empty, new(heroes), []), [], []),
        ];
    }

    private static UiHarness Match()
    {
        var ui = new UiHarness(settings => settings.Current.ReadBuildsOnTab = true);
        MatchPageTests.SetUpMatch(ui);
        ui.ViewModel.Match.SelectedCutoff = MatchViewModel.CutoffPresets[0];
        ui.ViewModel.Match.SelectedRank = MatchViewModel.RankPresets[1];
        return ui;
    }

    /// <summary>Six tier 4 weapon items: 38,400 souls, the whole ramp, and every one of them weapon.</summary>
    private static void BuildWeapon(UiHarness ui)
    {
        var read = new HeroItems(0, Enumerable.Range(0, 6).Select(cell =>
            new CellRead(cell, CellKind.Item, ShopCategory.Weapon, null, 0.9, 0.2, true, 4)).ToList(), new BarsRead(0.9, 0.1, 0.1), false);
        ui.ViewModel.Match.Match.Builds.Apply(Hero, read, DateTimeOffset.UtcNow);
        ui.ViewModel.Match.Refresh();
    }

    private static Dictionary<string, double> Scores(UiHarness ui) =>
        ui.ViewModel.Match.Results.Entries.OfType<ResultRowViewModel>().ToDictionary(row => row.ItemId, row => row.Score.Value);

    [AvaloniaFact]
    public void TheFilterWaitsForBuildsAndTheCountsThatSayWhatIsUsual()
    {
        using var ui = Match();
        var match = ui.ViewModel.Match;
        Assert.True(match.ByBuilds);
        Assert.False(match.CanLeanOnBuilds);

        BuildWeapon(ui);
        Assert.True(match.HasBuilds);
        Assert.False(match.CanLeanOnBuilds);

        GiveCounts(ui.Data.Store);
        match.Refresh();
        Assert.True(match.CanLeanOnBuilds);
    }

    [AvaloniaFact]
    public void AWeaponHeavyEnemyMovesTheItemsThatAnswerBulletAndSpiritDamageAndTheFilterTakesItBack()
    {
        using var ui = Match();
        var match = ui.ViewModel.Match;
        GiveCounts(ui.Data.Store);
        match.Refresh();
        var before = Scores(ui);

        BuildWeapon(ui);
        var shifted = Scores(ui);

        var shift = BuildShifts.For(ui.Data.Store, match.Match).Of(Hero);
        Assert.NotNull(shift);
        Assert.True(shift.PointsOn(BuildShifts.BulletTrait) > 0);
        Assert.True(shift.PointsOn(BuildShifts.SpiritTrait) < 0);
        Assert.Contains(shifted, pair => pair.Value != before[pair.Key]);
        // Anything with a rule on neither trait is untouched.
        var matrix = ui.Data.Matrix;
        Assert.All(shifted.Where(pair => matrix.Line(pair.Key, BuildShifts.BulletTrait, Relation.Against) is null
                                          && matrix.Line(pair.Key, BuildShifts.SpiritTrait, Relation.Against) is null),
            pair => Assert.Equal(before[pair.Key], pair.Value, 6));

        match.ByBuilds = false;

        Assert.False(ui.Settings.Current.ResultsByBuilds);
        Assert.Equal(before, Scores(ui));
        match.ByBuilds = true;
        Assert.True(ui.Settings.Current.ResultsByBuilds);
        Assert.Equal(shifted, Scores(ui));
    }

    [AvaloniaFact]
    public void TheExplanationShowsTheMovedScoreOnTheHerosTraitLine()
    {
        using var ui = Match();
        var match = ui.ViewModel.Match;
        GiveCounts(ui.Data.Store);
        ui.Show();
        BuildWeapon(ui);
        var item = ui.Data.Store.Items.Keys.First(id => ui.Data.Matrix.Line(id, BuildShifts.BulletTrait, Relation.Against) is not null);

        match.Results.Select(match.Results.Entries.OfType<ResultRowViewModel>().Single(row => row.ItemId == item));

        var card = match.Explain.Contributions.Single(contribution => contribution.HeroId == Hero && contribution.RelationText == "ENEMY");
        var line = card.Traits.Single(trait => trait.TraitName.StartsWith("Deals Bullet Damage", StringComparison.Ordinal));
        Assert.Contains(" → ", line.TraitName, StringComparison.Ordinal);
        Assert.EndsWith("(built weapon)", line.TraitName, StringComparison.Ordinal);
        Assert.Contains("weapon", card.NoteTip, StringComparison.Ordinal);
        Assert.True(File.Exists(ui.Screenshot("match_build_shift_explain.png")));

        match.ByBuilds = false;
        Assert.DoesNotContain(" → ", match.Explain.Contributions.SelectMany(contribution => contribution.Traits).Select(trait => trait.TraitName).Aggregate("", (a, b) => a + b));
    }

    [AvaloniaFact]
    public void TheFiltersMenuOffersIt()
    {
        using var ui = Match();
        GiveCounts(ui.Data.Store);
        BuildWeapon(ui);
        ui.Show();
        var button = ui.Window.MatchPage.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "FiltersButton");

        button.Flyout!.ShowAt(button);
        UiHarness.Settle();

        var box = ((Control)((Flyout)button.Flyout).Content!).GetLogicalDescendants().OfType<CheckBox>()
            .Single(check => Equals(check.Content, MatchViewModel.ByBuildsLabel));
        Assert.True(box.IsEffectivelyEnabled);
        Assert.True(box.IsChecked);
        Assert.True(File.Exists(ui.Screenshot("match_filters_builds.png")));
        button.Flyout.Hide();
    }
}
