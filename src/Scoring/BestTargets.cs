namespace DeadlockAdvisor.Scoring;

/// <summary>
/// Scoring for items cast on one hero at a time (<see cref="Models.Item.SingleTarget"/>: Decay, Knockdown,
/// Rescue Beam). On each team relation the best target counts in full, the next ×½, then ×¼ and so on,
/// less what the same measure comes to for a typical team of that size. A plain sum would count "no use
/// against this hero" once per hero, when you'd just cast it on the best one.
/// </summary>
public static class BestTargets
{
    /// <summary>Each target after the best counts this much of the one before it.</summary>
    public const double Decay = 0.5;

    /// <summary>The most heroes a relation can count: six enemies.</summary>
    public const int MaxTeam = 6;

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
