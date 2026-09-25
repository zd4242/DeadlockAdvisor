using Avalonia.Input;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.ItemFormulas;
using DeadlockAdvisor.Features.ItemFormulas.ByItem;
using DeadlockAdvisor.Features.Shared.Modals.Choice;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

public sealed class ItemFormulasTests : IDisposable
{
    private const string FocusLens = "focus_lens";
    private const string SpiritDamage = "deals_spirit_damage_general";

    private readonly DataFixture _fixture = new();
    private readonly ItemFormulasViewModel _vm;

    public ItemFormulasTests()
    {
        _vm = new ItemFormulasViewModel(_fixture.Data, _fixture.Modals, _fixture.Settings);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _fixture.Dispose();
    }

    private DataStore Store => _fixture.Data.Store;

    private ByItemViewModel Select(string itemId)
    {
        var page = _vm.ByItem;
        page.SelectedRow = page.Items.Single(row => row.ItemId == itemId);
        return page;
    }

    private string Untagged() =>
        Store.ItemsSorted().First(item => Store.RuleCount(item.ItemId) == 0 && Store.DerivedRuleCount(item.ItemId) == 0).ItemId;

    // -- By Item --------------------------------------------------------------------

    [Fact]
    public void RelationsSharingACoefficientShareACard()
    {
        var page = Select(FocusLens);

        Assert.Equal(Store.RuleCount(FocusLens) - 1, page.Rules.Count);
        var shared = page.Rules.Single(card => card.IsWith);
        Assert.True(shared.IsAs);
        Assert.False(shared.IsAgainst);
        Assert.Equal("Focus Lens", page.DetailTitle);
        Assert.Equal("Tier 4 · spirit · 6400 souls · id: focus_lens", page.DetailSub);
        Assert.Equal("4 rules", page.SelectedRow!.RulesText);
    }

    [Fact]
    public void AddRulePicksTheFirstTraitWithoutAnAgainstRuleAtTwo()
    {
        var itemId = Untagged();
        var page = Select(itemId);
        Assert.NotNull(page.RulesHint);

        page.AddRuleCommand.Execute().Subscribe();
        page.AddRuleCommand.Execute().Subscribe();

        var categories = Store.CategoriesOrdered();
        Assert.Equal(2.0, Store.Coefficient(itemId, categories[0].CategoryId, Relation.Against));
        Assert.Equal(2.0, Store.Coefficient(itemId, categories[1].CategoryId, Relation.Against));
        Assert.Equal(2, page.Rules.Count);
        Assert.Null(page.RulesHint);
        Assert.Equal("2 rules", page.SelectedRow!.RulesText);
    }

    [Fact]
    public void ARelationAnotherCardAlreadyCoversIsRefused()
    {
        var page = Select(FocusLens);
        var card = page.Rules.First(card => card.IsAgainst && card.SelectedCategory!.CategoryId == SpiritDamage);
        var before = Store.ItemCoefficients.Count;

        card.ToggleRelationCommand.Execute(Relation.With).Subscribe();

        Assert.False(card.IsWith);
        Assert.Equal("Another rule on that trait already covers 'with'.", card.Explanation.Single().Text);
        Assert.Equal(before, Store.ItemCoefficients.Count);
    }

    [Fact]
    public void TheLastRelationOnACardCantBeTurnedOff()
    {
        var page = Select(FocusLens);
        var card = page.Rules.First(card => card.IsAgainst);

        card.ToggleRelationCommand.Execute(Relation.Against).Subscribe();

        Assert.True(card.IsAgainst);
    }

    [Fact]
    public void ACardPointedAtAPartlyUsedTraitSlidesOntoAFreeRelation()
    {
        var itemId = Untagged();
        var page = Select(itemId);
        page.AddRuleCommand.Execute().Subscribe();
        page.AddRuleCommand.Execute().Subscribe();
        var categories = Store.CategoriesOrdered();

        page.Rules[1].SelectedCategory = categories[0];

        Assert.Equal(2.0, Store.Coefficient(itemId, categories[0].CategoryId, Relation.Against));
        Assert.Equal(2.0, Store.Coefficient(itemId, categories[0].CategoryId, Relation.With));
        Assert.Equal(0, Store.Coefficient(itemId, categories[1].CategoryId, Relation.Against));
        // Same trait, same number: the two cards become one.
        var merged = Assert.Single(page.Rules);
        Assert.True(merged.IsAgainst && merged.IsWith);
    }

    [Fact]
    public void EditingACoefficientWritesEveryRelationOnTheCardAndUpdatesThePreview()
    {
        var page = Select(FocusLens);
        var card = page.Rules.Single(card => card.IsWith);
        var cards = page.Rules.ToList();

        card.Coefficient = 3.25m;

        Assert.Equal(3.2, Store.Coefficient(FocusLens, SpiritDamage, Relation.With));
        Assert.Equal(3.2, Store.Coefficient(FocusLens, SpiritDamage, Relation.As));
        Assert.Equal(cards, page.Rules);
        var with = page.Preview!.Single(group => group.Title == "WITH ALLY");
        Assert.StartsWith("+", with.Rows[0].Value);
    }

    [Fact]
    public void RemovingACardDeletesAllItsRelations()
    {
        var page = Select(FocusLens);

        page.Rules.Single(card => card.IsWith).RemoveCommand.Execute().Subscribe();

        Assert.Equal(0, Store.Coefficient(FocusLens, SpiritDamage, Relation.With));
        Assert.Equal(0, Store.Coefficient(FocusLens, SpiritDamage, Relation.As));
        Assert.Equal("2 rules", page.SelectedRow!.RulesText);
        Assert.Equal(2.0, _fixture.Saved().Coefficient(FocusLens, SpiritDamage, Relation.Against));
    }

    [Fact]
    public void CopyRulesReplacesTheItemsRulesWithAnothers()
    {
        ChoiceModalViewModel? modal = null;
        using var _ = _fixture.Modals.ShowModalObservable.Subscribe(shown => modal = shown as ChoiceModalViewModel);
        var itemId = Untagged();
        var page = Select(itemId);

        page.CopyRulesCommand.Execute().Subscribe();
        Assert.NotNull(modal);
        modal.SelectedIndex = modal.Choices.ToList().FindIndex(choice => choice.StartsWith("Focus Lens  (T4, 4 rules)"));
        modal.OkCommand.Execute().Subscribe();

        Assert.Equal(Store.RulesForItem(FocusLens).Select(rule => (rule.CategoryId, rule.Relation, rule.Coefficient)),
            Store.RulesForItem(itemId).Select(rule => (rule.CategoryId, rule.Relation, rule.Coefficient)));
    }

    [Fact]
    public void TheListFiltersByTierUntaggedAndSearch()
    {
        var page = _vm.ByItem;

        page.SetTierFilterCommand.Execute(4).Subscribe();
        Assert.All(page.Items, row => Assert.Equal(4, row.Tier));
        Assert.True(page.TierPills[4].IsChecked);
        Assert.False(page.TierPills[0].IsChecked);

        page.SetTierFilterCommand.Execute(0).Subscribe();
        page.UntaggedOnly = true;
        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, row => Assert.Equal("untagged", row.RulesText));

        page.UntaggedOnly = false;
        page.SearchText = " Silence ";
        Assert.All(page.Items, row => Assert.True(Store.ItemMatches(row.ItemId, "silence")));
        Assert.Contains(page.Items, row => row.ItemId == FocusLens);

        // Tier, then name, whatever the filter did in between.
        page.SearchText = "";
        Assert.Equal(Store.ItemsSorted().Select(item => item.ItemId), page.Items.Select(row => row.ItemId));
    }

    [Fact]
    public void ThePreviewSplitsSumsByTraitOnlyWhenThereIsMoreThanOne()
    {
        var page = Select(FocusLens);

        var against = page.Preview!.Single(group => group.Title == "AGAINST ENEMY");
        Assert.Equal(2, against.TraitColumns);
        Assert.True(against.Rows[0].IsBest);
        Assert.All(against.Rows.Skip(1), row => Assert.False(row.IsBest && row.Value != against.Rows[0].Value));
        Assert.Contains(" × ", against.Rows[0].Tip);

        var with = page.Preview!.Single(group => group.Title == "WITH ALLY");
        Assert.Equal(0, with.TraitColumns);
        Assert.All(with.Rows, row => Assert.Empty(row.Pieces));
    }

    [Theory]
    [InlineData(0.44, "0.4")]
    [InlineData(2.5, "2.5")]
    [InlineData(3.0, "3")]
    [InlineData(12.5, "12")]
    [InlineData(13.5, "14")]
    [InlineData(-10.4, "-10")]
    public void PreviewAmountsAreWholeFromTenUp(double value, string expected)
    {
        Assert.Equal(expected, FormulaText.Amount(value));
    }

    // -- By Trait -------------------------------------------------------------------

    [Fact]
    public void ADigitSetsTheCoefficientAndDropsToTheNextItem()
    {
        var page = _vm.ByTrait;
        var rows = page.Rows.Take(3).ToList();

        page.HandleText("4");
        page.HandleText("-");
        page.HandleText("2");
        Assert.Equal(4, Store.Coefficient(rows[0].ItemId, SpiritDamage, Relation.Against));
        Assert.Equal(-2, Store.Coefficient(rows[1].ItemId, SpiritDamage, Relation.Against));
        Assert.Same(rows[2], page.CurrentRow);

        page.HandleKey(Key.Up, KeyModifiers.None);
        page.HandleKey(Key.Back, KeyModifiers.None);
        Assert.Equal(0, Store.Coefficient(rows[1].ItemId, SpiritDamage, Relation.Against));
        Assert.Same(rows[2], page.CurrentRow);
        Assert.Equal(4, _fixture.Saved().Coefficient(rows[0].ItemId, SpiritDamage, Relation.Against));
    }

    [Fact]
    public void EditsDontResortTheGrid()
    {
        var page = _vm.ByTrait;
        page.SortCommand.Execute(3).Subscribe();
        page.SortCommand.Execute(3).Subscribe();
        var order = page.Rows.ToList();

        page.CurrentRow = order[^1];
        page.HandleText("9");

        Assert.Equal(order, page.Rows);
        Assert.Equal(9, order[^1].Coefficient);
    }

    [Fact]
    public void TaggedOnlyShowsItemsWithATypedOrStatDerivedNumber()
    {
        var page = _vm.ByTrait;
        page.TaggedOnly = true;

        Assert.NotEmpty(page.Rows);
        Assert.All(page.Rows, row => Assert.True(row.IsTagged));
        Assert.Contains(page.Rows, row => row.Coefficient == 0 && row.FromStats != 0);
        Assert.Equal("3 item(s) typed + 21 from stats for this trait/relation · 42 rules total", page.Summary);
    }

    [Fact]
    public void TheWeightScalesTheWholeTraitAndOneRemovesIt()
    {
        var page = _vm.ByTrait;

        page.Weight = 0.504m;
        Assert.Equal(0.5, Store.TraitWeight(SpiritDamage, Relation.Against));
        Assert.Contains("all scaled × 0.5", page.Summary);

        page.Weight = 1;
        Assert.DoesNotContain(new Models.WeightKey(SpiritDamage, Relation.Against), Store.TraitWeights.Keys);
    }

    [Fact]
    public void AStatRuleStartsAtTheSuggestedRateAndASwappedStatIsReSuggested()
    {
        var page = _vm.ByTrait;
        page.SetRelationCommand.Execute(Relation.With).Subscribe();
        Assert.Empty(page.StatRules);
        Assert.False(page.HasStatRules);

        page.AddStatRuleCommand.Execute().Subscribe();
        var row = Assert.Single(page.StatRules);
        var stat = row.SelectedChoice!.Stat;
        var rule = Store.StatRulesFor(SpiritDamage, Relation.With).Single();
        Assert.Equal((stat, Store.SuggestPerUnit(stat), 0.5), (rule.Stat, rule.PerUnit, rule.ConditionalFactor));

        var other = row.Choices.First(choice => choice.Stat != stat);
        row.SelectedChoice = other;
        rule = Store.StatRulesFor(SpiritDamage, Relation.With).Single();
        Assert.Equal((other.Stat, Store.SuggestPerUnit(other.Stat)), (rule.Stat, rule.PerUnit));

        page.StatRules.Single().RemoveCommand.Execute().Subscribe();
        Assert.Empty(Store.StatRulesFor(SpiritDamage, Relation.With));
        Assert.Empty(page.StatRules);
    }

    [Fact]
    public void SwitchingPanelsResyncsTheOther()
    {
        var page = Select(Untagged());
        var itemId = page.SelectedRow!.ItemId;
        _vm.SelectedTab = 1;
        var row = _vm.ByTrait.Rows.Single(candidate => candidate.ItemId == itemId);
        _vm.ByTrait.CurrentRow = row;
        _vm.ByTrait.HandleText("5");

        _vm.SelectedTab = 0;

        Assert.Equal("1 rule", page.SelectedRow!.RulesText);
        Assert.Single(page.Rules);
    }
}
