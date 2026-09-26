using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Board;

/// <summary>One of a team's places on the match bar, kept for the life of the board and refilled in place.</summary>
public class RosterSlotViewModel(Role team) : ViewModelBase
{
    /// <summary>Ally or Enemy.</summary>
    public Role Team { get; } = team;

    [Reactive] public string? HeroId { get; private set; }
    [Reactive] public string HeroName { get; private set; } = "";
    [Reactive] public bool IsSelf { get; private set; }
    [Reactive] public bool InLane { get; private set; }

    /// <summary>The hero's latest net worth in souls, if it's been read.</summary>
    [Reactive] public int? NetWorth { get; private set; }

    /// <summary>How that moved since the reading before, e.g. "+3.1k in 2 min".</summary>
    [Reactive] public string? NetWorthChange { get; private set; }

    /// <summary>Whether the match has any net worth, so every slot makes room for it alike.</summary>
    [Reactive] public bool ShowsNetWorth { get; set; }

    public void Fill(string heroId, string heroName, bool isSelf, bool inLane, int? netWorth, string? netWorthChange)
    {
        HeroId = heroId;
        HeroName = heroName;
        IsSelf = isSelf;
        InLane = inLane;
        NetWorth = netWorth;
        NetWorthChange = netWorthChange;
    }

    public void Clear()
    {
        HeroId = null;
        HeroName = "";
        IsSelf = false;
        InLane = false;
        NetWorth = null;
        NetWorthChange = null;
    }
}
