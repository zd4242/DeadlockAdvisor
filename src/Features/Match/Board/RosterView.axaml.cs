using Avalonia.ReactiveUI;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Features.Match.Board;

public partial class RosterView : ReactiveUserControl<MatchBoardViewModel>
{
    public RosterView()
    {
        InitializeComponent();

        AddHandler(RosterSlot.RightClickedEvent, (_, e) => RoleMenu.Show(ViewModel, e));
        AddHandler(RosterSlot.RemovedEvent, (_, e) => ViewModel?.SetRole(e.HeroId, Role.None));
        AddHandler(RosterSlot.LaneToggledEvent, (_, e) => ViewModel?.ToggleLane(e.HeroId));
        AddHandler(RosterSlot.EmptyClickedEvent, (_, e) =>
        {
            if (e.Source is RosterSlot slot)
                ViewModel?.EmptySlotClicked(slot.Team);
        });
    }
}
