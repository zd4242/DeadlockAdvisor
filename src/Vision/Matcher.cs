namespace DeadlockAdvisor.Vision;

/// <param name="HeroIndex">The hero this slot got, or null when nothing cleared <see cref="Matcher.MinScore"/>.</param>
/// <param name="RunnerUp">The best hero this slot didn't get, whether or not another slot claimed them.</param>
public sealed record SlotAssignment(int Slot, int? HeroIndex, double Score, double Margin, int? RunnerUp)
{
    public bool IsConfident => Matcher.IsConfident(HeroIndex is not null, Score, Margin);
}

/// <summary>
/// A score matrix → one hero per slot. Picking each slot's best independently happily puts one hero
/// in three slots; Deadlock never repeats a hero in a match, so the slots are solved together, and
/// a slot that's sure of its hero takes them off the table for one that was only guessing. Greedy
/// descending selection is good enough at 12 slots by 38 heroes.
/// </summary>
public static class Matcher
{
    /// <summary>Below this a slot is left unidentified rather than guessed: a dead player's silhouette carries no hero at all.</summary>
    public const float MinScore = 0.30f;

    // A confident read is one detection would apply without anyone checking it. On the labelled
    // captures a hero in their own slot scores 0.76 or better nineteen times in twenty, and no other
    // hero has scored above 0.41 in a slot that isn't theirs.
    public const float ConfidentScore = 0.40f;

    /// <summary>How far clear of the runner-up a match has to be to count as confident.</summary>
    public const float ClearMargin = 0.08f;

    public static bool IsConfident(bool hasHero, double score, double margin) => hasHero && score >= ConfidentScore && margin >= ClearMargin;

    /// <summary>A row's hero indices, best first; ties go to the later index, as numpy's reversed argsort gives.</summary>
    public static int[] Ranked(float[] row) =>
        Enumerable.Range(0, row.Length).OrderByDescending(i => row[i]).ThenByDescending(i => i).ToArray();

    public static List<SlotAssignment> Assign(float[][] scores, float minScore = MinScore)
    {
        var slotCount = scores.Length;
        var heroCount = slotCount == 0 ? 0 : scores[0].Length;
        var takenSlot = new bool[slotCount];
        var takenHero = new bool[heroCount];
        var chosen = new int?[slotCount];
        var chosenScore = new double[slotCount];

        var order = Enumerable.Range(0, slotCount * heroCount)
            .OrderByDescending(flat => scores[flat / heroCount][flat % heroCount])
            .ThenByDescending(flat => flat);
        foreach (var flat in order)
        {
            var (slot, hero) = (flat / heroCount, flat % heroCount);
            var value = scores[slot][hero];
            if (value < minScore)
                break;
            if (takenSlot[slot] || takenHero[hero])
                continue;
            takenSlot[slot] = takenHero[hero] = true;
            chosen[slot] = hero;
            chosenScore[slot] = value;
        }

        var result = new List<SlotAssignment>();
        for (var slot = 0; slot < slotCount; slot++)
        {
            var row = scores[slot];
            var ranked = Ranked(row);
            if (chosen[slot] is not { } hero)
            {
                result.Add(new SlotAssignment(slot, null, heroCount > 0 ? row[ranked[0]] : -1.0, 0.0, heroCount > 0 ? ranked[0] : null));
                continue;
            }
            int? runner = ranked.Where(h => h != hero).Select(h => (int?)h).FirstOrDefault();
            var margin = chosenScore[slot] - (runner is { } r ? row[r] : -1.0);
            result.Add(new SlotAssignment(slot, hero, chosenScore[slot], margin, runner));
        }
        return result;
    }
}
