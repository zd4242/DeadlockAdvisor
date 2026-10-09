using System.Text;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Formats;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

/// <summary>Every file the app writes must come out byte-identical to its golden copy.</summary>
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
    public void LoadThenSaveIsByteIdentical(string fileName)
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

        if (Golden.Updating)
        {
            Golden.CopyFile(data.File(fileName), "csv_roundtrip", fileName);
            return;
        }
        AssertEx.BytesEqual(Golden.PathOf("csv_roundtrip", fileName), data.File(fileName));
    }

    [Fact]
    public void StatRuleEdgeCasesReadAndWriteBackUnchanged()
    {
        using var data = new TempDirectory();
        File.Copy(Golden.PathOf("format_edge", "stat_rules_input.csv"), data.File(DataStore.StatRulesFile));
        var store = new DataStore(data.Path);

        store.LoadStatRules();
        store.SaveStatRules();

        AssertEx.BytesEqual(Golden.PathOf("format_edge", "stat_rules_output.csv"), data.File(DataStore.StatRulesFile));
    }

    [Fact]
    public void JsonWithEscapesFloatsAndNestingRoundTrips()
    {
        using var data = new TempDirectory();
        var expected = Golden.PathOf("format_edge", "meta_output.json");
        var store = new DataStore(data.Path)
        {
            MatchMeta = DataJson.Parse(File.ReadAllText(expected, Encoding.UTF8))!.AsObject(),
        };

        store.SaveMatchLift();

        AssertEx.BytesEqual(expected, data.File(DataStore.MatchMetaFile));
        AssertEx.BytesEqual(Golden.PathOf("format_edge", "empty_match_lift_output.csv"), data.File(DataStore.MatchLiftFile));
    }

    [Fact]
    public void TooltipsWithMarkupAndUnicodeWriteTheGoldenFile()
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
                        new[] { new TooltipStat("-0.5 m", "Move Speed", false, true, BoonScale: 4) }.ToEquatableList()),
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

    [Fact]
    public void BackupsFromEarlierHoursAndDaysSurviveABurst()
    {
        using var data = new TempDirectory();
        var now = new DateTime(2026, 10, 9, 15, 30, 0);
        // A burst of 20 saves in the last 20 minutes, then one copy every 40 minutes back 3 weeks.
        var stamps = Enumerable.Range(0, 20).Select(i => now.AddMinutes(-i))
            .Concat(Enumerable.Range(1, 21 * 36).Select(i => now.AddMinutes(-20 - i * 40))).ToList();
        foreach (var stamp in stamps)
            File.WriteAllText(data.File($"trait_weights.{stamp:yyyyMMdd-HHmmss}.csv"), "old");

        BackedUpFile.TrimBackups(data.Path, "trait_weights", ".csv", now);

        var kept = Directory.GetFiles(data.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        var times = kept.Select(name => DateTime.ParseExact(name!.Substring(14, 15), "yyyyMMdd-HHmmss", null)).ToList();
        // The newest 12 are kept whole.
        Assert.All(stamps.Take(12), stamp => Assert.Contains(stamp, times));
        // Every one of the last 24 hours that has a copy still has its newest.
        var lastDay = stamps.Where(stamp => now - stamp <= BackedUpFile.HourlyWindow).GroupBy(stamp => (stamp.Date, stamp.Hour));
        Assert.All(lastDay, hour => Assert.Contains(hour.Max(), times));
        // Every day of the last 14 has its newest.
        var lastFortnight = stamps.Where(stamp => now - stamp <= BackedUpFile.DailyWindow).GroupBy(stamp => stamp.Date);
        Assert.All(lastFortnight, day => Assert.Contains(day.Max(), times));
        // Nothing else stays: the three-week-old copies and the burst's middle are gone.
        Assert.DoesNotContain(times, time => now - time > BackedUpFile.DailyWindow);
        Assert.InRange(kept.Count, 13, 12 + 24 + 14);
    }

    [Fact]
    public void BackupsOfAnyExtensionAreTrimmedAndOnlyThatFilesOwn()
    {
        using var data = new TempDirectory();
        var now = new DateTime(2026, 10, 9, 12, 0, 0);
        for (var day = 1; day <= 15; day++)
        {
            File.WriteAllText(data.File($"items.200001{day:00}-000000.json"), "old");
            File.WriteAllText(data.File($"item_stats.200001{day:00}-000000.json"), "another file");
        }
        File.WriteAllText(data.File("items.notes.json"), "not a backup");
        File.WriteAllText(data.File("items.20000101-000000.csv"), "another extension");

        BackedUpFile.TrimBackups(data.Path, "items", ".json", now);

        Assert.Equal(12, Directory.GetFiles(data.Path, "items.2*.json").Length);
        Assert.False(File.Exists(data.File("items.20000101-000000.json")));
        Assert.True(File.Exists(data.File("items.20000115-000000.json")));
        Assert.Equal(15, Directory.GetFiles(data.Path, "item_stats.*").Length);
        Assert.True(File.Exists(data.File("items.notes.json")));
        Assert.True(File.Exists(data.File("items.20000101-000000.csv")));
    }

    [Fact]
    public void JsonSavesKeepTheFirstCopyOfASecondAndTrimLikeCsv()
    {
        using var data = new TempDirectory();
        var path = data.File("item_tooltips.json");
        File.WriteAllText(path, "first");
        var backups = Path.Combine(data.Path, BackedUpFile.BackupFolderName);
        Directory.CreateDirectory(backups);
        for (var day = 10; day < 25; day++)
            File.WriteAllText(Path.Combine(backups, $"item_tooltips.200001{day:00}-000000.json"), "old");

        BackedUpFile.Write(path, "second"u8);
        BackedUpFile.Write(path, "third"u8);

        Assert.Equal("third", File.ReadAllText(path));
        var kept = Directory.GetFiles(backups, "item_tooltips.*").ToList();
        Assert.Equal(BackedUpFile.Keep, kept.Count);
        // Both writes fell in one second (or the second one's copy is the newer): the first copy holds the original.
        Assert.Contains(kept, file => File.ReadAllText(file) == "first");
    }
}
