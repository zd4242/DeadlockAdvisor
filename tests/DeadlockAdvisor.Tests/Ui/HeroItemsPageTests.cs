using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.HeroItems;
using DeadlockAdvisor.Tests.Fakes;

namespace DeadlockAdvisor.Tests.Ui;

public class HeroItemsPageTests
{
    /// <summary>Its tab is there without the model editors, and opens on your hero from the Match page.</summary>
    [AvaloniaFact]
    public async Task TheTabOpensOnYourHerosItems()
    {
        using var ui = new UiHarness();
        await SyntheticItemStatsApi.DownloadAsync(ui.Data.Store);
        ui.Data.NotifyReplaced();
        ui.ViewModel.Match.Match.SetRole("dynamo", Role.Self);
        ui.Show();

        Click(ui.Window, ui.Window.PageTabs.GetVisualDescendants().OfType<ListBoxItem>().Single(item => Equals(item.Content, "Hero Items")));

        Assert.True(ui.ViewModel.IsHeroItemsPage);
        var page = ui.ViewModel.HeroItems;
        Assert.Equal("dynamo", page.SelectedHero!.HeroId);
        var rows = ui.Window.HeroItemsPage.GetVisualDescendants().OfType<ItemsControl>().Single(list => list.Name == "Table")
            .GetVisualDescendants().OfType<Border>().Count(border => border.Classes.Contains("row"));
        Assert.True(rows > 5);
        ui.Screenshot("hero_items.png");

        var winRate = ui.Window.HeroItemsPage.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Win rate"));
        Click(ui.Window, winRate);
        Assert.Equal(HeroItemSort.WinRate, page.SortColumn);
        Assert.Equal("Win rate ▾", winRate.Content);
    }

    [AvaloniaFact]
    public void WithoutMatchDataItSaysHowToGetIt()
    {
        using var ui = new UiHarness(settings => settings.Current.LastPage = 1);
        ui.Show();

        Assert.True(ui.ViewModel.IsHeroItemsPage);
        var texts = ui.Window.HeroItemsPage.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible)
            .Select(text => text.Text).ToList();
        Assert.Contains(texts, text => text?.Contains("Data → Download Match Data") == true);
        ui.Screenshot("hero_items_empty.png");
    }

    /// <summary>Every row's menu names its wiki page; the formula's entry comes with the model editors.</summary>
    [AvaloniaFact]
    public async Task ARowsMenuLinksItsWikiPageAndOpensItsFormulaWhileTheEditorsShow()
    {
        using var ui = new UiHarness(settings => settings.Current.LastPage = 1);
        await SyntheticItemStatsApi.DownloadAsync(ui.Data.Store);
        ui.Data.NotifyReplaced();
        ui.Show();
        var row = Rows(ui).First();
        var item = (HeroItemRowViewModel)row.DataContext!;

        var plain = RightClick(ui.Window, row);
        Assert.Equal([WikiMenuItem.Text], plain.Select(entry => entry.Header));
        Assert.Equal(item.Name, plain.OfType<WikiMenuItem>().Single().Page);

        ui.ViewModel.Settings.General.ShowModelEditors = true;
        UiHarness.Settle();
        Assert.Equal(["Go to Item Formula", WikiMenuItem.Text], RightClick(ui.Window, row).Select(entry => entry.Header));
        Assert.NotSame(Rows(ui).First().ContextMenu, Rows(ui).Skip(1).First().ContextMenu);

        var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), ui.Window)!.Value;
        ui.Window.MouseDown(at, MouseButton.Right);
        ui.Window.MouseUp(at, MouseButton.Right);
        UiHarness.Settle();
        var goTo = ui.Window.GetVisualDescendants().OfType<MenuItem>().Single(entry => Equals(entry.Header, "Go to Item Formula"));
        Click(ui.Window, goTo);

        Assert.True(ui.ViewModel.IsItemFormulasPage);
        Assert.Equal(item.ItemId, ui.ViewModel.ItemFormulas.ByItem.CurrentItem?.ItemId);
    }

    /// <summary>The patch box opens a list of them to tick, and says what's ticked.</summary>
    [AvaloniaFact]
    public async Task ThePatchBoxTicksSeveralPatches()
    {
        using var ui = new UiHarness(settings => settings.Current.LastPage = 1);
        await SyntheticItemStatsApi.DownloadAsync(ui.Data.Store);
        ui.Data.NotifyReplaced();
        ui.Show();
        var page = ui.ViewModel.HeroItems;
        var box = ui.Window.HeroItemsPage.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PatchPicker");
        Assert.Equal("Patch 09-29", box.Content);

        box.Flyout!.ShowAt(box);
        UiHarness.Settle();
        var checkBoxes = ((Control)((Flyout)box.Flyout).Content!).GetLogicalDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(["Patch 09-29", "Patch 09-16"], checkBoxes.Select(checkBox => checkBox.Content));
        checkBoxes[1].IsChecked = true;
        UiHarness.Settle();
        ui.Screenshot("hero_items_patches.png");

        Assert.Equal("Patches 09-16 – 09-29", box.Content);
        Assert.Contains(" over 2 patches · ", page.Summary);
        box.Flyout.Hide();
    }

    [AvaloniaFact]
    public async Task AboveAndBelowAverageWinRatesAreTintedBehindTheirNumbers()
    {
        using var ui = new UiHarness(settings => settings.Current.LastPage = 1);
        await SyntheticItemStatsApi.DownloadAsync(ui.Data.Store);
        ui.Data.NotifyReplaced();
        ui.Show();

        var chips = Rows(ui).Select(row => (Row: (HeroItemRowViewModel)row.DataContext!,
            Chip: row.GetVisualDescendants().OfType<Border>().Single(border => border.Child is TextBlock text && text.Classes.Contains("winRate"))))
            .ToList();

        Assert.Contains(chips, pair => pair.Row.AboveAverage);
        Assert.Contains(chips, pair => pair.Row.BelowAverage);
        Assert.All(chips, pair =>
        {
            var tinted = pair.Chip.Background is ISolidColorBrush { Color.A: > 0 };
            Assert.Equal(pair.Row.AboveAverage || pair.Row.BelowAverage, tinted);
        });
    }

    private static List<Border> Rows(UiHarness ui) =>
        ui.Window.HeroItemsPage.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("row")).ToList();

    /// <summary>Right-clicks <paramref name="target"/> and returns the shown entries of the menu that opens, closing it again.</summary>
    private static List<MenuItem> RightClick(Window window, Control target)
    {
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        UiHarness.Settle();
        var menu = window.GetVisualDescendants().OfType<ContextMenu>().Single(candidate => candidate.IsOpen);
        var shown = menu.GetVisualDescendants().OfType<MenuItem>().Where(entry => entry.IsVisible).ToList();
        menu.Close();
        UiHarness.Settle();
        return shown;
    }

    private static void Click(TopLevel root, Visual target)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root)!.Value;
        root.MouseDown(at, MouseButton.Left);
        root.MouseUp(at, MouseButton.Left);
        UiHarness.Settle();
    }
}
