using System.Reactive.Disposables;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Enums;
using ReactiveUI;

namespace DeadlockAdvisor.Features.Match.Board;

public partial class MatchBoardView : ReactiveUserControl<MatchBoardViewModel>
{
    public MatchBoardView()
    {
        InitializeComponent();

        AddHandler(HeroTile.ClickedEvent, (_, e) => ViewModel?.TileClicked(e.HeroId));
        AddHandler(HeroTile.DoubleClickedEvent, (_, e) => ViewModel?.TileDoubleClicked(e.HeroId));
        AddHandler(HeroTile.RightClickedEvent, (_, e) => ShowRoleMenu(e));
        AddHandler(SlotStrip.RightClickedEvent, (_, e) => ShowRoleMenu(e));
        AddHandler(SlotStrip.RemovedEvent, (_, e) => ViewModel?.SetRole(e.HeroId, Role.None));
        AddHandler(SlotStrip.LaneToggledEvent, (_, e) => ViewModel?.ToggleLane(e.HeroId));
        AllyStrip.AddHandler(SlotStrip.EmptyClickedEvent, (_, _) => ViewModel?.AllySlotClicked());
        EnemyStrip.AddHandler(SlotStrip.EmptyClickedEvent, (_, _) => ViewModel?.EnemySlotClicked());

        // The search box keeps focus through the keyboard flow: Enter assigns, Up/Down move the pick.
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);

        this.WhenActivated(disposables =>
        {
            ViewModel!.ViewInteraction
                .Subscribe(action =>
                {
                    if (action == MatchBoardViewModel.FocusSearchAction)
                        FocusSearch();
                })
                .DisposeWith(disposables);
        });
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null)
            return;
        switch (e.Key)
        {
            case Key.Enter:
                ViewModel.AssignHighlighted();
                e.Handled = true;
                break;
            case Key.Down:
                ViewModel.MoveHighlight(1);
                e.Handled = true;
                break;
            case Key.Up:
                ViewModel.MoveHighlight(-1);
                e.Handled = true;
                break;
            case Key.Escape when !string.IsNullOrEmpty(SearchBox.Text):
                ViewModel.SearchText = "";
                e.Handled = true;
                break;
        }
    }

    /// <summary>The explicit role menu, from a right click on a palette tile or a roster slot.</summary>
    private void ShowRoleMenu(HeroEventArgs e)
    {
        var vm = ViewModel;
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
