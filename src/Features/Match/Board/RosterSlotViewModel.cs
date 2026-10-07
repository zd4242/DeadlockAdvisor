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

    /// <summary>The hero's latest net worth in souls, if it's been read.</summary>
    [Reactive] public int? NetWorth { get; private set; }

    /// <summary>How that moved since the reading before, e.g. "+3.1k in 2 min".</summary>
    [Reactive] public string? NetWorthChange { get; private set; }

    /// <summary>Whether the match has any net worth, so every slot makes room for it alike.</summary>
    [Reactive] public bool ShowsNetWorth { get; set; }

    /// <summary>An enemy the recommendations are focused on.</summary>
    [Reactive] public bool IsFocused { get; private set; }

    /// <summary>An enemy counting for less because another is focused.</summary>
    [Reactive] public bool IsDimmed { get; private set; }

    /// <summary>An enemy rated on some trait, so focusing on them means something.</summary>
    [Reactive] public bool CanFocus { get; private set; } = true;

    public void Fill(string heroId, string heroName, bool isSelf, int? netWorth, string? netWorthChange)
    {
        HeroId = heroId;
        HeroName = heroName;
        IsSelf = isSelf;
        NetWorth = netWorth;
        NetWorthChange = netWorthChange;
    }

    /// <summary>Where the slot's hero stands with the focus: focused, dimmed while another is, and whether a click can focus them.</summary>
    public void SetFocus(bool isFocused, bool isDimmed, bool canFocus)
    {
        IsFocused = isFocused;
        IsDimmed = isDimmed;
        CanFocus = canFocus;
    }

    public void Clear()
    {
        HeroId = null;
        HeroName = "";
        IsSelf = false;
        NetWorth = null;
        NetWorthChange = null;
        SetFocus(false, false, true);
    }
}
