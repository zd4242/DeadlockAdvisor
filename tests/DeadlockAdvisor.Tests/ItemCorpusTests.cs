using System.Text.Json.Nodes;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Tests.Ui;
using DeadlockAdvisor.Vision;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Tests;

/// <summary>
/// The item reader against every labelled board in Golden/vision/items/corpus: real scoreboards, native 2560×1440, from the start of
/// a match to its end, with dead heroes, a Street Brawl, a tooltip and a glare over the panels. Each is read as captured and
/// shrunk to what 1080p and 900p screens draw. Golden/vision/items/corpus_report.json pins which cells are read confidently right;
/// a deliberate change regenerates it (DEADLOCK_UPDATE_GOLDENS=1) and the diff shows what it won and lost.
/// mockups/items_report.md explains the numbers.
/// </summary>
public class ItemCorpusTests
{
    private static readonly Lazy<List<ItemOutcome>> _outcomes = new(() => ItemEval.Run(Boards, ItemData.Templates));

    private static List<ItemCorpusBoard> Boards => ItemCorpusBoard.LoadAll(ItemData.CorpusDir);

    [Fact]
    public void TheCorpusReadsNoWorseThanItsReport()
    {
        var outcomes = _outcomes.Value;
        Assert.NotEmpty(outcomes);
        var report = UiHarness.MockupPath("items_report.md");
        File.WriteAllText(report, ItemEval.Markdown(outcomes, ItemData.Templates));

        var pinned = ItemEval.Pinned(outcomes, ItemData.Templates);
        if (Golden.Updating)
        {
            Golden.WriteJson("vision/items/corpus_report.json", pinned);
            return;
        }
        var golden = Golden.Json("vision/items/corpus_report.json")["captures"]!.AsObject();
        var problems = new List<string>();
        foreach (var (name, node) in pinned["captures"]!.AsObject())
        {
            var now = node!.AsObject();
            if (golden[name]?.AsObject() is not { } was)
            {
                problems.Add($"{name}: not in the report; regenerate it");
                continue;
            }
            var lost = Keys(was["confident_correct"]).Except(Keys(now["confident_correct"])).ToList();
            if (lost.Count > 0)
                problems.Add($"{name}: no longer confidently right in cell(s) {string.Join(", ", lost)}");
            foreach (var key in new[] { "confident_wrong", "unverified", "category_wrong", "kind_wrong", "tier_wrong", "bar_disagree", "count_wrong", "cover_wrong" })
            {
                var added = Keys(now[key]).Except(Keys(was[key])).ToList();
                if (added.Count > 0)
                    problems.Add($"{name}: newly {key.Replace('_', ' ')} at {string.Join(", ", added)}");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems) + $"\n(see {report})");
    }

    /// <summary>A confident read is given without anyone checking it, so on the labelled boards none may be wrong, at any size, and none may be of a cell no one has named.</summary>
    [Fact]
    public void NoCellIsReadConfidentlyWrong()
    {
        foreach (var outcome in _outcomes.Value)
        {
            var tally = ItemEval.Tally(outcome, ItemData.Templates);
            Assert.True(tally.ConfidentWrong.Count == 0, $"{outcome.Name}: confidently wrong at {string.Join(", ", tally.ConfidentWrong)}");
            Assert.True(tally.Unverified.Count == 0, $"{outcome.Name}: confident but unnamed in the label at {string.Join(", ", tally.Unverified)}: look at it and name it");
        }
    }

    /// <summary>Every cell of an item the label has, in the corpus's boards as captured, is found with the shop category its tag says.</summary>
    [Fact]
    public void EveryOccupiedCellHasItsCategory()
    {
        foreach (var outcome in _outcomes.Value.Where(outcome => outcome.Scale == 1.0))
        {
            var tally = ItemEval.Tally(outcome, ItemData.Templates);
            Assert.True(tally.KindWrong.Count == 0 && tally.CategoryWrong.Count == 0,
                $"{outcome.Name}: kind wrong at {string.Join(", ", tally.KindWrong)}, category wrong at {string.Join(", ", tally.CategoryWrong)}");
            Assert.True(tally.CountWrong.Count == 0, $"{outcome.Name}: the cells of slot(s) {string.Join(", ", tally.CountWrong)} were miscounted");
        }
    }

    /// <summary>Unreadable cells say so: the report has the unknown share per size, and it can't be everything.</summary>
    [Fact]
    public void MostCellsAreReadAtEverySize()
    {
        foreach (var size in _outcomes.Value.GroupBy(outcome => outcome.Height))
        {
            var tallies = size.Select(outcome => ItemEval.Tally(outcome, ItemData.Templates)).ToList();
            var cells = tallies.Sum(tally => tally.Cells);
            var known = tallies.Sum(tally => tally.ConfidentCorrect.Count);
            Assert.True(known >= 0.6 * cells, $"{size.Key}p: {known} of {cells} cells identified");
        }
    }

    /// <summary>
    /// The tier numeral is read against references cut from the same board, so each of the labelled cells has to read as its item's
    /// tier by the references of the others, or not at all. At 1440p the shapes read nearly all of them; at 1080p they can't and the
    /// ink of the strokes does (<see cref="TierReader"/>), for most.
    /// </summary>
    [Fact]
    public void ANumeralReadsAsItsItemsTierOrNotAtAll()
    {
        var templates = ItemData.Templates;
        var cellsAt = new Dictionary<int, int>();
        var unreadAt = new Dictionary<int, int>();
        var wrong = new List<string>();
        foreach (var outcome in _outcomes.Value.Where(outcome => outcome.Scale == 1.0))
        {
            var (image, grid) = ItemEval.Scaled(outcome.Board, outcome.Scale);
            var lattice = outcome.Read.Lattice;
            var place = ItemPlace.Default(grid, grid.Centers()) with
            {
                SlideLeft = lattice.SlideLeft, SlideRight = lattice.SlideRight, SlideY = lattice.SlideY, Scale = lattice.Scale,
            };
            var known = outcome.Heroes.SelectMany(hero => hero.Cells.Where(cell => cell.IsIdentified).Select(cell => (hero.Slot, cell))).ToList();
            var reader = new TierReader();
            var frames = known.Select(pair => (Tier: templates.Items[templates.IndexOf(pair.cell.ItemId!)!.Value].Tier, Frame: TierReader.Read(image, place, pair.Slot, pair.cell.Cell))).ToList();
            for (var i = 0; i < frames.Count; i++)
                reader.Add(frames[i].Tier, frames[i].Frame, known[i].Slot, known[i].cell.Cell);
            for (var i = 0; i < frames.Count; i++)
            {
                cellsAt[outcome.Height] = cellsAt.GetValueOrDefault(outcome.Height) + 1;
                if (reader.Classify(frames[i].Frame, known[i].Slot, known[i].cell.Cell) is not { } tier)
                    unreadAt[outcome.Height] = unreadAt.GetValueOrDefault(outcome.Height) + 1;
                else if (tier != frames[i].Tier)
                    wrong.Add($"{outcome.Name} {known[i].Slot}:{known[i].cell.Cell} {known[i].cell.ItemId} reads as tier {tier}, not {frames[i].Tier}");
            }
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        Assert.True(cellsAt[1440] > 500 && cellsAt[1080] > 500, $"only {cellsAt[1440]} cells at 1440p and {cellsAt[1080]} at 1080p");
        Assert.True(unreadAt.GetValueOrDefault(1440) <= 0.05 * cellsAt[1440], $"{unreadAt.GetValueOrDefault(1440)} of {cellsAt[1440]} numerals read as no tier at 1440p");
        Assert.True(unreadAt.GetValueOrDefault(1080) <= 0.25 * cellsAt[1080], $"{unreadAt.GetValueOrDefault(1080)} of {cellsAt[1080]} numerals read as no tier at 1080p");
    }

    /// <summary>
    /// A window or the kill feed drawn over a hero's first cells hides their tags, not the heroes' other items: what is under it reads as
    /// unseen, never as an invented item, and the rest of the board reads as it did.
    /// </summary>
    [Fact]
    public void ACoveredCellIsUnseenAndTheRestIsUnchanged()
    {
        var board = Boards.Single(board => board.Name == "late_clean");
        var plain = ItemReader.Read(board.Image, board.Label.Grid, ItemData.Templates, board.Label.BlankSlots);
        var grid = board.Label.Grid;
        var place = ItemPlace.Default(grid, grid.Centers()) with { SlideY = plain.Lattice.SlideY, Scale = plain.Lattice.Scale, SlideLeft = plain.Lattice.SlideLeft, SlideRight = plain.Lattice.SlideRight };

        // The first two rows of the first slot, over a flat colour.
        var covered = new RgbImage(board.Image.Width, board.Image.Height, (byte[])board.Image.Pixels.Clone());
        var (left, top) = place.Cell(0, 0);
        var (right, bottom) = place.Cell(0, 3);
        for (var y = (int)(top - place.Side); y < (int)(bottom + place.Side / 2); y++)
        {
            for (var x = (int)(left - place.Side); x < (int)(right + place.Side / 2); x++)
                (covered.Pixels[(y * covered.Width + x) * 3], covered.Pixels[(y * covered.Width + x) * 3 + 1], covered.Pixels[(y * covered.Width + x) * 3 + 2]) = (90, 60, 40);
        }

        var read = ItemReader.Read(covered, grid, ItemData.Templates, board.Label.BlankSlots);
        var before = plain.Heroes.Single(hero => hero.Slot == 0).Cells;
        var after = read.Heroes.Single(hero => hero.Slot == 0);
        Assert.False(after.Covered);
        Assert.Equal(before.Count, after.Count);
        Assert.All(after.Cells.Take(4), cell => Assert.Equal(CellKind.Unseen, cell.Kind));
        Assert.Equal(before.Skip(4).Select(cell => cell.ItemId), after.Cells.Skip(4).Select(cell => cell.ItemId));
        foreach (var hero in read.Heroes.Where(hero => hero.Slot != 0))
            Assert.Equal(plain.Heroes.Single(other => other.Slot == hero.Slot).Cells.Select(cell => cell.ItemId), hero.Cells.Select(cell => cell.ItemId));
    }

    /// <summary>A panel with a window over it is flat where the bars are: nothing of that slot is read, and it says so, however many items it holds.</summary>
    [Fact]
    public void ASlotUnderAWindowIsCovered()
    {
        var board = Boards.Single(board => board.Name == "late_clean");
        var grid = board.Label.Grid;
        var covered = new RgbImage(board.Image.Width, board.Image.Height, (byte[])board.Image.Pixels.Clone());
        var centers = grid.Centers();
        for (var y = (int)(grid.Top + 2.0 * grid.Pitch); y < covered.Height; y++)
        {
            for (var x = (int)(centers[3] - 0.5 * grid.Pitch); x < (int)(centers[4] + 0.5 * grid.Pitch); x++)
                (covered.Pixels[(y * covered.Width + x) * 3], covered.Pixels[(y * covered.Width + x) * 3 + 1], covered.Pixels[(y * covered.Width + x) * 3 + 2]) = (40, 100, 70);
        }
        var read = ItemReader.Read(covered, grid, ItemData.Templates, board.Label.BlankSlots);
        Assert.All(read.Heroes.Where(hero => hero.Slot is 3 or 4), hero =>
        {
            Assert.True(hero.Covered);
            Assert.Empty(hero.Cells);
        });
        Assert.All(read.Heroes.Where(hero => hero.Slot is not (3 or 4)), hero => Assert.False(hero.Covered));
    }

    /// <summary>A Street Brawl's blank slots aren't read at all.</summary>
    [Fact]
    public void BlankSlotsAreSkipped()
    {
        var board = Boards.Single(board => board.Name == "brawl_dead_view");
        var read = ItemReader.Read(board.Image, board.Label.Grid, ItemData.Templates, board.Label.BlankSlots);
        Assert.Equal(8, read.Heroes.Count);
        Assert.DoesNotContain(read.Heroes, hero => board.Label.BlankSlots.Contains(hero.Slot));
    }

    /// <summary>The Street Brawl's grid is a per cent out in pitch and in the gap between teams, which the reader fits for itself.</summary>
    [Fact]
    public void AGridThatIsOutInPitchIsFittedToTheTags()
    {
        var board = Boards.Single(board => board.Name == "brawl_dead_view");
        var read = ItemReader.Read(board.Image, board.Label.Grid, ItemData.Templates, board.Label.BlankSlots);
        Assert.InRange(read.Lattice.Scale, 0.985, 0.995);
        Assert.True(read.Lattice.Cells >= 60, $"{read.Lattice.Cells} cells lined up");
        Assert.True(Math.Abs(read.Lattice.SlideLeft - read.Lattice.SlideRight) > 0.02, "the teams were not slid apart");
    }

    /// <summary>An item the game draws in a way the API's art doesn't is a file dropped into the item's folder, not a change of code.</summary>
    [Fact]
    public void AVariantOfAnIconIsMatchedToo()
    {
        using var temp = new TempDirectory();
        var items = Golden.LoadStore().Items.Values.Where(item => item.ItemId is "tankbuster" or "compress_cooldown").ToList();
        Directory.CreateDirectory(Path.Combine(temp.Path, "tankbuster"));
        File.Copy(Path.Combine(ItemData.ArtDir, "tankbuster.png"), Path.Combine(temp.Path, "tankbuster.png"));
        File.Copy(Path.Combine(ItemData.ArtDir, "compress_cooldown.png"), Path.Combine(temp.Path, "tankbuster", "variant_01.png"));
        File.Copy(Path.Combine(ItemData.ArtDir, "compress_cooldown.png"), Path.Combine(temp.Path, "compress_cooldown.png"));
        var templates = ItemTemplates.Load(temp.Path, items);
        Assert.Equal(3, templates.Icons.Count);
        Assert.Equal(2, templates.Items.Count);
        Assert.Equal(templates.Icons.Where(icon => icon.ItemId == "tankbuster").Count(), 2);
    }

    private static IEnumerable<string> Keys(JsonNode? node) => node?.AsArray().Select(key => key!.ToString()) ?? [];
}
