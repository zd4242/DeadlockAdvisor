namespace DeadlockAdvisor.Models;

/// <summary>
/// The line-ups the match has been through, for undo and redo. It is told what the match is after each change
/// and keeps the one before it, so every way of changing the match is covered without each one saying so.
/// </summary>
public sealed class MatchHistory
{
    public const int Limit = 100;

    private readonly List<MatchPicks> _undo = [];
    private readonly List<MatchPicks> _redo = [];
    private MatchPicks _now;

    public MatchHistory(MatchPicks start) => _now = start;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Note the match as it is now: a change from the last line-up is a step to undo, and ends any redo.</summary>
    public void Record(MatchPicks picks)
    {
        if (picks.Equals(_now))
            return;
        _undo.Add(_now);
        if (_undo.Count > Limit)
            _undo.RemoveAt(0);
        _redo.Clear();
        _now = picks;
    }

    /// <summary>The line-up before the last change, now the current one; null if there's nothing to undo.</summary>
    public MatchPicks? Undo() => Step(_undo, _redo);

    public MatchPicks? Redo() => Step(_redo, _undo);

    /// <summary>Start over from <paramref name="picks"/>, such as after the heroes themselves changed.</summary>
    public void Reset(MatchPicks picks)
    {
        _undo.Clear();
        _redo.Clear();
        _now = picks;
    }

    private MatchPicks? Step(List<MatchPicks> from, List<MatchPicks> to)
    {
        if (from.Count == 0)
            return null;
        to.Add(_now);
        _now = from[^1];
        from.RemoveAt(from.Count - 1);
        return _now;
    }
}
