using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Features.Match.Board;

/// <summary>The explicit role menu, from a right click on a palette tile or a match bar slot.</summary>
public static class RoleMenu
{
    public static void Show(MatchBoardViewModel? vm, HeroEventArgs e)
    {
        if (vm is null || !vm.HasHero(e.HeroId) || e.Source is not Control target)
            return;

        var heroId = e.HeroId;
        var current = vm.RoleOf(heroId);
        var items = new List<Control>();
        foreach (var role in MatchBoardViewModel.ModeOrder)
        {
            items.Add(MenuItem($"Set as {role.Label()}", current == role, () => vm.SetRole(heroId, role)));
        }
        if (current is Role.Ally or Role.Enemy)
        {
            items.Add(new Separator());
            items.Add(MenuItem("In my lane", vm.IsInLane(heroId), () => vm.ToggleLane(heroId)));
        }
        if (current != Role.None)
        {
            items.Add(new Separator());
            items.Add(MenuItem("Remove from match", null, () => vm.SetRole(heroId, Role.None)));
        }

        var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
        menu.Open(target);
    }

    private static MenuItem MenuItem(string header, bool? isChecked, Action action)
    {
        var item = new MenuItem { Header = header };
        if (isChecked is not null)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = isChecked.Value;
        }
        item.Click += (_, _) => action();
        return item;
    }
}
