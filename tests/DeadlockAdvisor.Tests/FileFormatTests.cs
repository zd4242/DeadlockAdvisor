using System.Text;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Formats;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

/// <summary>Every file the app writes must come out byte-identical to what the Python app writes.</summary>
public class FileFormatTests
{
    public static TheoryData<string> WrittenFiles() =>
    [
        DataStore.HeroesFile, DataStore.ItemsFile, DataStore.HeroScoresFile, DataStore.ItemCoefficientsFile,
        DataStore.TraitWeightsFile, DataStore.StatRulesFile, DataStore.ItemStatsFile, DataStore.ItemTooltipsFile,
        DataStore.MatchLiftFile, DataStore.MatchMetaFile,
    ];

    [Theory]
    [MemberData(nameof(WrittenFiles))]
    public void LoadThenSaveMatchesThePythonApp(string fileName)
    {
        using var data = Golden.CopyData();
        var store = DataStore.Load(data.Path);

        store.SaveHeroes();
        store.SaveItems();
        store.SaveHeroScores();
        store.SaveItemCoefficients();
        store.SaveTraitWeights();
        store.SaveStatRules();
        store.SaveItemStats();
        store.SaveItemTooltips();
        store.SaveMatchLift();

        AssertEx.BytesEqual(Golden.PathOf("csv_roundtrip", fileName), data.File(fileName));
    }

    [Fact]
    public void StatRuleEdgeCasesReadAndWriteLikeThePythonApp()
    {
        using var data = new TempDirectory();
        File.Copy(Golden.PathOf("format_edge", "stat_rules_input.csv"), data.File(DataStore.StatRulesFile));
        var store = new DataStore(data.Path);

        store.LoadStatRules();
        store.SaveStatRules();

        AssertEx.BytesEqual(Golden.PathOf("format_edge", "stat_rules_output.csv"), data.File(DataStore.StatRulesFile));
    }

    [Fact]
    public void JsonWithEscapesFloatsAndNestingRoundTripsLikeThePythonApp()
    {
        using var data = new TempDirectory();
        var expected = Golden.PathOf("format_edge", "meta_output.json");
        var store = new DataStore(data.Path)
        {
            MatchMeta = PythonJson.Parse(File.ReadAllText(expected, Encoding.UTF8))!.AsObject(),
        };

        store.SaveMatchLift();

        AssertEx.BytesEqual(expected, data.File(DataStore.MatchMetaFile));
        AssertEx.BytesEqual(Golden.PathOf("format_edge", "empty_match_lift_output.csv"), data.File(DataStore.MatchLiftFile));
    }

    [Fact]
    public void TooltipsWithMarkupAndUnicodeWriteLikeThePythonApp()
    {
        using var data = new TempDirectory();
        var store = new DataStore(data.Path);
        store.ItemTooltips["café_item"] = new ItemTooltip("café_item",
            new[]
            {
                new TooltipSection("active", "16s", new[]
                {
                    new TooltipBlock("Deals <span style=\"color:#CE90FF\">Spirit</span> – café &amp; \"more\"",
                        new[] { new TooltipStat("+20%", "Max ⚡ Ammo", SpiritScale: 0.0055) }.ToEquatableList(),
                        new[] { new TooltipStat("", "Silenced", true, false) }.ToEquatableList(),
                        new[] { new TooltipStat("-0.5 m", "Move Speed", false, true) }.ToEquatableList()),
                    TooltipBlock.Empty,
                }.ToEquatableList()),
            }.ToEquatableList(),
            new[] { "base_item" }.ToEquatableList());
        store.ItemTooltips["plain"] = new ItemTooltip("plain", EquatableList<TooltipSection>.Empty, EquatableList<string>.Empty);

        store.SaveItemTooltips();

        AssertEx.BytesEqual(Golden.PathOf("format_edge", "tooltips_output.json"), data.File(DataStore.ItemTooltipsFile));
    }

    [Fact]
    public void CsvReaderHandlesQuotesLineBreaksAndShortRows()
    {
        var rows = CsvReader.Read("﻿a,b,c\r\n1,\"x, \"\"y\"\"\",3\n\n\"multi\r\nline\",,\r\nshort\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal("x, \"y\"", rows[0].Get("b"));
        Assert.Equal("multi\r\nline", rows[1].Get("a"));
        Assert.Equal("", rows[1].Get("c"));
        Assert.Equal("short", rows[2].Get("a"));
        Assert.Null(rows[2].Get("b"));
    }

    [Fact]
    public void CsvWriterQuotesOnlyWhatNeedsIt()
    {
        var bytes = CsvWriter.ToBytes(["a", "b"], [["plain", "with,comma"], ["quote\"d", "line\nbreak"], ["", " spaced "]]);

        Assert.Equal("a,b\r\nplain,\"with,comma\"\r\n\"quote\"\"d\",\"line\nbreak\"\r\n, spaced \r\n", Encoding.UTF8.GetString(bytes));
        Assert.NotEqual(0xEF, bytes[0]);
    }

    [Fact]
    public void SavesKeepTheNewestTwelveBackups()
    {
        using var data = new TempDirectory();
        var backups = Path.Combine(data.Path, BackedUpFile.BackupFolderName);
        Directory.CreateDirectory(backups);
        for (var day = 10; day < 25; day++)
            File.WriteAllText(Path.Combine(backups, $"trait_weights.200001{day:00}-000000.csv"), "old");
        File.WriteAllText(Path.Combine(backups, "item_stats.20000101-000000.csv"), "another file's backup");
        File.WriteAllText(data.File(DataStore.TraitWeightsFile), "category_id,relation,weight\r\n");

        var store = new DataStore(data.Path);
        store.SetTraitWeight("trait", Relation.Against, 2);
        store.SaveTraitWeights();

        var kept = Directory.GetFiles(backups, "trait_weights.*").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(BackedUpFile.Keep, kept.Count);
        Assert.DoesNotContain("trait_weights.20000110-000000.csv", kept);
        Assert.Contains("trait_weights.20000124-000000.csv", kept);
        // The newest is the copy just taken, holding the file's previous contents.
        Assert.Equal("category_id,relation,weight\r\n", File.ReadAllText(Path.Combine(backups, kept[^1]!)));
        Assert.True(File.Exists(Path.Combine(backups, "item_stats.20000101-000000.csv")));
        Assert.False(File.Exists(data.File(DataStore.TraitWeightsFile + ".tmp")));
    }
}
