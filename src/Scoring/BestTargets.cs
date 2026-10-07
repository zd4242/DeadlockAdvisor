using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Scoring;

/// <summary>
/// Scoring for items cast on one hero at a time (<see cref="Models.Item.CastOn"/>: Decay, Knockdown,
/// Rescue Beam). On the teams <see cref="AppliesTo"/> names the best target counts in full, the next ×½, then ×¼ and so on,
/// less what the same measure comes to for a typical team of that size. A plain sum would count "no use
/// against this hero" once per hero, when you'd just cast it on the best one.
/// </summary>
public static class BestTargets
{
    /// <summary>Each target after the best counts this much of the one before it.</summary>
    public const double Decay = 0.5;

    /// <summary>The most heroes a relation can count: six enemies.</summary>
    public const int MaxTeam = 6;

    /// <summary>
    /// Whether an item cast on one hero of <paramref name="castOn"/>'s team scores <paramref name="relation"/>
    /// on its best targets. The team it's cast on always does. An ally-cast save also answers one enemy
    /// threat per cast, so its enemies do too; an enemy-cast debuff helps every ally who hits the target, so
    /// its allies sum.
    /// </summary>
    public static bool AppliesTo(Relation castOn, Relation relation) =>
        relation == castOn || castOn == Relation.With && relation == Relation.Against;

    /// <summary>1 for the best target, ½ for the second, ¼ for the third…</summary>
    public static double RankFactor(int rank) => Math.Pow(Decay, rank - 1);

    /// <summary>The weights best first, each times its <see cref="RankFactor"/>.</summary>
    public static double Sum(IEnumerable<double> weights)
    {
        var total = 0.0;
        var rank = 1;
        foreach (var weight in weights.OrderByDescending(weight => weight))
            total += RankFactor(rank++) * weight;
        return total;
    }

    /// <summary>
    /// <see cref="Sum"/>'s average over every team of <paramref name="count"/> heroes drawn from the
    /// roster's weights. Exact: with the roster sorted best first, the r-th best of a team is roster
    /// hero j with probability C(j−1, r−1) · C(N−j, count−r) / C(N, count).
    /// </summary>
    public static double Expected(IReadOnlyList<double> roster, int count)
    {
        var total = roster.Count;
        if (count <= 0 || count > total)
            return 0.0;
        var sorted = roster.OrderByDescending(weight => weight).ToList();
        var teams = Choose(total, count);
        var expected = 0.0;
        for (var rank = 1; rank <= count; rank++)
        {
            var atRank = 0.0;
            for (var j = rank; j <= total - count + rank; j++)
                atRank += sorted[j - 1] * Choose(j - 1, rank - 1) * Choose(total - j, count - rank);
            expected += RankFactor(rank) * atRank / teams;
        }
        return expected;
    }

    /// <summary>
    /// <see cref="Expected(IReadOnlyList{double}, int)"/> for a team <paramref name="focused"/> of whose
    /// <paramref name="count"/> heroes count <paramref name="ratio"/> times the rest (<see cref="FocusWeights"/>):
    /// the average over every team and every choice of who in it is focused. Exact: a hero's term is their
    /// weighted weight at the rank factor of how many teammates beat it, and how many do is a count of
    /// draws without replacement, the focused ones first, then the rest.
    /// </summary>
    public static double Expected(IReadOnlyList<double> roster, int count, int focused, double ratio)
    {
        var total = roster.Count;
        if (focused <= 0 || ratio == 1)
            return Expected(roster, count);
        if (count <= 0 || count > total)
            return 0.0;
        focused = Math.Min(focused, count);
        var expected = 0.0;
        for (var hero = 0; hero < total; hero++)
        {
            expected += (double)focused / total * ratio * roster[hero]
                        * RankFactorOf(roster, hero, ratio * roster[hero], ratio, focused - 1, count - focused);
            if (count > focused)
                expected += (double)(count - focused) / total * roster[hero]
                            * RankFactorOf(roster, hero, roster[hero], ratio, focused, count - focused - 1);
        }
        return expected;
    }

    /// <summary>
    /// The average <see cref="RankFactor"/> of a hero counting <paramref name="value"/> on a team that also
    /// draws <paramref name="focused"/> heroes counted ×<paramref name="ratio"/> from the rest of the roster, then
    /// <paramref name="unfocused"/> counted ×1. A teammate beats the hero by counting more, or as much from
    /// earlier in the roster, so ties rank as a sort would.
    /// </summary>
    private static double RankFactorOf(IReadOnlyList<double> roster, int hero, double value, double ratio, int focused, int unfocused)
    {
        bool Beats(double other, int index) => other > value || other == value && index < hero;

        // Teammates by whether they'd beat the hero when focused (first) and when not (second).
        var counts = new int[2, 2];
        for (var index = 0; index < roster.Count; index++)
        {
            if (index != hero)
                counts[Beats(ratio * roster[index], index) ? 1 : 0, Beats(roster[index], index) ? 1 : 0]++;
        }

        var others = roster.Count - 1;
        var draws = Choose(others, focused);
        var expected = 0.0;
        for (var both = 0; both <= Math.Min(focused, counts[1, 1]); both++)
        for (var focusedOnly = 0; focusedOnly <= Math.Min(focused - both, counts[1, 0]); focusedOnly++)
        for (var unfocusedOnly = 0; unfocusedOnly <= Math.Min(focused - both - focusedOnly, counts[0, 1]); unfocusedOnly++)
        {
            var neither = focused - both - focusedOnly - unfocusedOnly;
            if (neither > counts[0, 0])
                continue;
            var chance = Choose(counts[1, 1], both) * Choose(counts[1, 0], focusedOnly) * Choose(counts[0, 1], unfocusedOnly)
                         * Choose(counts[0, 0], neither) / draws;
            // The unfocused draw comes from whoever is left, and beats the hero by its unfocused value.
            var beaten = counts[1, 1] - both + counts[0, 1] - unfocusedOnly;
            expected += chance * Math.Pow(Decay, both + focusedOnly) * DecayOver(others - focused, beaten, unfocused);
        }
        return expected;
    }

    /// <summary>The average of <see cref="Decay"/> to the power of how many of <paramref name="draws"/> from <paramref name="population"/> are among its <paramref name="hits"/>.</summary>
    private static double DecayOver(int population, int hits, int draws)
    {
        var expected = 0.0;
        for (var hit = Math.Max(0, draws - (population - hits)); hit <= Math.Min(draws, hits); hit++)
            expected += Choose(hits, hit) * Choose(population - hits, draws - hit) / Choose(population, draws) * Math.Pow(Decay, hit);
        return expected;
    }

    private static double Choose(int n, int k)
    {
        if (k < 0 || k > n)
            return 0.0;
        var result = 1.0;
        for (var i = 1; i <= k; i++)
            result = result * (n - k + i) / i;
        return result;
    }
}
