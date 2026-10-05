using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
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

    private static void Click(TopLevel root, Visual target)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root)!.Value;
        root.MouseDown(at, MouseButton.Left);
        root.MouseUp(at, MouseButton.Left);
        UiHarness.Settle();
    }
}
