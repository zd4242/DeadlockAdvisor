namespace DeadlockAdvisor.Models;

/// <summary>One reading of the match's net worth: each hero read, in souls, and when.</summary>
public sealed record NetWorthSnapshot(DateTimeOffset At, IReadOnlyDictionary<string, int> Souls);

/// <summary>
/// How the match's net worth has moved: every reading, oldest first. A reading can leave a hero out
/// (a side whose pills didn't add up to its total is dropped), so a hero's latest value is the most
/// recent reading that has them.
/// </summary>
public sealed class NetWorthHistory
{
    /// <summary>Two hours of reads a minute apart, far more than a match lasts.</summary>
    public const int MaxSnapshots = 120;

    private readonly List<NetWorthSnapshot> _snapshots = [];

    public IReadOnlyList<NetWorthSnapshot> Snapshots => _snapshots;

    public bool IsEmpty => _snapshots.Count == 0;

    public DateTimeOffset? LatestAt => _snapshots.Count == 0 ? null : _snapshots[^1].At;

    /// <summary>Keep a reading; one with nobody in it isn't worth a place.</summary>
    public void Add(NetWorthSnapshot snapshot)
    {
        if (snapshot.Souls.Count == 0)
            return;
        _snapshots.Add(snapshot);
        if (_snapshots.Count > MaxSnapshots)
            _snapshots.RemoveRange(0, _snapshots.Count - MaxSnapshots);
    }

    public void Clear() => _snapshots.Clear();

    public int? Latest(string heroId)
    {
        foreach (var (souls, _) in Readings(heroId))
            return souls;
        return null;
    }

    /// <summary>How much a hero's net worth moved between their last two readings, and over how long.</summary>
    public (int Souls, TimeSpan Over)? Change(string heroId)
    {
        var readings = Readings(heroId).Take(2).ToList();
        if (readings.Count < 2)
            return null;
        return (readings[0].Souls - readings[1].Souls, readings[0].At - readings[1].At);
    }

    private IEnumerable<(int Souls, DateTimeOffset At)> Readings(string heroId)
    {
        for (var i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].Souls.TryGetValue(heroId, out var souls))
                yield return (souls, _snapshots[i].At);
        }
    }
}
