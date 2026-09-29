using DeadlockAdvisor.Core;

namespace DeadlockAdvisor.Tests;

public class FuzzyMatchTests
{
    private static readonly string[] _heroes = ["Abrams", "Grey Talon", "Ivy", "Mo & Krill", "Seven", "Venator", "Viscous", "Vyper"];

    [Theory]
    [InlineData("gt", "Grey Talon")]
    [InlineData("GREY", "Grey Talon")]
    [InlineData("grey t", "Grey Talon")]
    [InlineData("mk", "Mo & Krill")]
    [InlineData("mcg", "McGinnis")]
    [InlineData("ven", "Seven")]
    public void FindsRunsOfLettersAndStartsOfWordsIgnoringCaseAndSpaces(string query, string text) =>
        Assert.NotNull(FuzzyMatch.Score(query, text));

    [Theory]
    [InlineData("tg", "Grey Talon")]
    [InlineData("abramss", "Abrams")]
    [InlineData("x", "Abrams")]
    public void MissesWhenTheLettersArentThereInOrder(string query, string text) =>
        Assert.Null(FuzzyMatch.Score(query, text));

    [Theory]
    [InlineData("vnr", "Venator")]
    [InlineData("la", "Holliday")]
    [InlineData("slow", "Compress Cooldown")]
    [InlineData("heal", "Alchemical Fire")]
    public void MissesLettersScatteredThroughWords(string query, string text) =>
        Assert.Null(FuzzyMatch.Score(query, text));

    [Theory]
    [InlineData("ven", "Venator")]
    [InlineData("v", "Venator")]
    [InlineData("mk", "Mo & Krill")]
    [InlineData("iv", "Ivy")]
    public void TheStartsOfWordsAndRunsOfLettersRankFirst(string query, string first) =>
        Assert.Equal(first, FuzzyMatch.Filter(_heroes, query, hero => hero)[0]);

    [Fact]
    public void AnEmptyQueryKeepsEverythingInOrder() =>
        Assert.Equal(_heroes, FuzzyMatch.Filter(_heroes, "  ", hero => hero));

    [Fact]
    public void TiesKeepTheirOrder() =>
        Assert.Equal(["Venator", "Viscous", "Vyper", "Ivy", "Seven"], FuzzyMatch.Filter(_heroes, "v", hero => hero));
}
