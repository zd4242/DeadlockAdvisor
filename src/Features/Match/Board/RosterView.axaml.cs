using Avalonia.ReactiveUI;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Features.Match.Board;

public partial class RosterView : ReactiveUserControl<MatchBoardViewModel>
{
    public RosterView()
    {
        InitializeComponent();

        AddHandler(RosterSlot.MenuRequestedEvent, (_, e) => RoleMenu.Show(ViewModel, e));
        AddHandler(RosterSlot.SelfRequestedEvent, (_, e) => ViewModel?.SetRole(e.HeroId, Role.Self));
        AddHandler(RosterSlot.FocusRequestedEvent, (_, e) => ViewModel?.ToggleFocus(e.HeroId));
        AddHandler(RosterSlot.RemovedEvent, (_, e) => ViewModel?.SetRole(e.HeroId, Role.None));
        AddHandler(RosterSlot.EmptyClickedEvent, (_, e) =>
        {
            if (e.Source is RosterSlot slot)
                ViewModel?.EmptySlotClicked(slot.Team);
        });
    }
}
