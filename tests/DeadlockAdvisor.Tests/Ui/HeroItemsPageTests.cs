using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Controls.Art;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.HeroItems;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Contracts;
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

        var winRate = ui.Window.HeroItemsPage.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "WIN RATE"));
        Click(ui.Window, winRate);
        Assert.Equal(HeroItemSort.WinRate, page.SortColumn);
        Assert.Equal("WIN RATE ▾", winRate.Content);
    }

    /// <summary>The filter button opens the match mode, the rank range and the change columns' switch.</summary>
    [AvaloniaFact]
    public async Task TheFiltersMenuHoldsTheMatchModeTheRanksAndTheChangeColumns()
    {
        using var ui = new UiHarness(settings => settings.Current.LastPage = 1);
        await SyntheticItemStatsApi.DownloadAsync(ui.Data.Store);
        ui.Data.NotifyReplaced();
        ui.Show();
        var page = ui.ViewModel.HeroItems;
        var button = ui.Window.HeroItemsPage.GetVisualDescendants().OfType<Button>().Single(candidate => candidate.Name == "FiltersButton");
        List<string> HeaderTexts() => ui.Window.HeroItemsPage.GetVisualDescendants().OfType<Button>()
            .Where(candidate => candidate.Classes.Contains("header") && candidate.IsEffectivelyVisible).Select(candidate => (string)candidate.Content!).ToList();
        // Off until asked for: the two change columns have no header either.
        Assert.Equal(6, HeaderTexts().Count);
        Assert.Contains("HERO FIT", HeaderTexts());
        Assert.DoesNotContain(HeaderTexts(), text => text.Contains('Δ'));

        button.Flyout!.ShowAt(button);
        UiHarness.Settle();
        var content = (Control)((Flyout)button.Flyout).Content!;
        var combos = content.GetLogicalDescendants().OfType<ComboBox>().ToList();
        Assert.Equal(3, combos.Count);
        Assert.Contains(combos, combo => combo.ItemsSource == page.Ranks && Equals(combo.SelectedItem, page.From));
        var showChanges = content.GetLogicalDescendants().OfType<CheckBox>().Single();
        showChanges.IsChecked = true;
        UiHarness.Settle();
        ui.Screenshot("hero_items_filters.png");

        // Only ranked matches have a rank: the other modes grey the range out.
        var rankBoxes = combos.Where(combo => combo.ItemsSource == page.Ranks).ToList();
        Assert.Equal(2, rankBoxes.Count);
        Assert.All(rankBoxes, combo => Assert.True(combo.IsEffectivelyEnabled));
        page.SelectedMode = HeroItemsViewModel.Modes.Single(mode => mode.Mode == MatchMode.All);
        UiHarness.Settle();
        Assert.All(rankBoxes, combo => Assert.False(combo.IsEffectivelyEnabled));
        Assert.Equal([page.Ranks[0], page.Ranks[^1]], rankBoxes.Select(combo => combo.SelectedItem));
        ui.Screenshot("hero_items_filters_all.png");
        page.SelectedMode = HeroItemsViewModel.Modes.Single(mode => mode.Mode == MatchMode.Ranked);
        UiHarness.Settle();

        Assert.True(page.ShowChanges);
        Assert.Contains(HeaderTexts(), text => text.StartsWith("WIN Δ"));
        Assert.Contains(HeaderTexts(), text => text.StartsWith("USAGE Δ"));
        var row = Rows(ui).First();
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Classes.Contains("change") && text.IsEffectivelyVisible);
        ui.Screenshot("hero_items_changes.png");
        button.Flyout.Hide();
    }

    /// <summary>Fluent keeps a slider's track at the top of a box taller than its thumb, which sat it above its label.</summary>
    [AvaloniaFact]
    public async Task TheUsageSliderIsCentredWithItsLabelAndValue()
    {
        using var ui = new UiHarness(settings => settings.Current.LastPage = 1);
        await SyntheticItemStatsApi.DownloadAsync(ui.Data.Store);
        ui.Data.NotifyReplaced();
        ui.Show();
        var page = ui.Window.HeroItemsPage;
        double MiddleOf(Visual visual) => visual.TranslatePoint(new Point(0, visual.Bounds.Height / 2), ui.Window)!.Value.Y;
        var slider = page.GetVisualDescendants().OfType<Slider>().Single();
        var track = slider.GetVisualDescendants().OfType<Border>().First(border => border.Name == "TrackBackground");
        var label = page.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "Min usage");
        var value = page.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == ui.ViewModel.HeroItems.MinUsageText);

        Assert.Equal(MiddleOf(label), MiddleOf(track), 1);
        Assert.Equal(MiddleOf(value), MiddleOf(track), 1);
        ui.Screenshot("hero_items_usage.png");
    }

    /// <summary>The box sizes to the hero it shows, so it's given the room the widest name needs: one width for every hero, none cut off.</summary>
    [AvaloniaFact]
    public async Task TheHeroBoxFitsEveryHeroAtOneWidth()
    {
        using var ui = new UiHarness(settings => settings.Current.LastPage = 1);
        await SyntheticItemStatsApi.DownloadAsync(ui.Data.Store);
        ui.Data.NotifyReplaced();
        ui.Show();
        var page = ui.ViewModel.HeroItems;
        var picker = ui.Window.HeroItemsPage.GetVisualDescendants().OfType<SearchComboBox>().Single();
        var widths = new List<double>();

        foreach (var hero in page.Heroes)
        {
            page.SelectedHero = hero;
            UiHarness.Settle();
            var shown = picker.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.GetVisualDescendants().OfType<ArtImage>().Any() && panel.IsEffectivelyVisible);
            Assert.True(shown.Bounds.Width >= shown.DesiredSize.Width - 0.5, $"{hero.HeroName} is cut off");
            widths.Add(picker.Bounds.Width);
        }

        Assert.Single(widths.Distinct());
    }

    /// <summary>Each header is as wide as its column's cells, and sits at the same place, so it's centred over them.</summary>
    [AvaloniaFact]
    public async Task EachHeaderSitsOverItsColumn()
    {
        using var ui = new UiHarness(settings =>
        {
            settings.Current.LastPage = 1;
            settings.Current.HeroItemsShowChanges = true;
        });
        await SyntheticItemStatsApi.DownloadAsync(ui.Data.Store);
        ui.Data.NotifyReplaced();
        ui.Show();
        var page = ui.ViewModel.HeroItems;
        var row = Rows(ui).First();
        var headers = ui.Window.HeroItemsPage.GetVisualDescendants().OfType<Button>().Where(candidate => candidate.Classes.Contains("header")).ToList();
        Assert.Equal(8, headers.Count);

        double CentreOf(Visual visual) => visual.TranslatePoint(new Point(visual.Bounds.Width / 2, 0), ui.Window)!.Value.X;
        var cells = row.GetVisualDescendants().OfType<Control>().Where(control => control.Parent is Grid grid && grid == row.Child).ToList();
        Assert.Equal(8, cells.Count);
        // Cost is right-aligned and the item left-aligned, so their cells' edges line up with the header's instead of their middles.
        foreach (var (header, cell) in headers.Zip(cells).Skip(2))
            Assert.InRange(CentreOf(header) - CentreOf(cell), -1, 1);
        Assert.Equal(row.TranslatePoint(new Point(), ui.Window)!.Value.X + row.Padding.Left,
            cells[0].TranslatePoint(new Point(), ui.Window)!.Value.X, 1);
        Assert.Equal(cells[0].TranslatePoint(new Point(), ui.Window)!.Value.X, headers[0].TranslatePoint(new Point(), ui.Window)!.Value.X, 1);
        Assert.True(page.ShowChanges);
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

    /// <summary>Each rank box shows the badge of the rank it sets, and nothing, not even a gap, while that art is missing.</summary>
    [AvaloniaFact]
    public async Task TheRankBoxesShowTheirBadgesOnceTheArtIsThere()
    {
        using var ui = new UiHarness(settings => settings.Current.LastPage = 1);
        await SyntheticItemStatsApi.DownloadAsync(ui.Data.Store);
        ui.Data.NotifyReplaced();
        ui.Art.SetAssetsDir(ui.Data.AssetsDir);
        ui.Show();
        var filters = ui.Window.HeroItemsPage.GetVisualDescendants().OfType<Button>().Single(candidate => candidate.Name == "FiltersButton");
        filters.Flyout!.ShowAt(filters);
        UiHarness.Settle();
        List<ArtImage> Badges() => ((Control)((Flyout)filters.Flyout).Content!).GetVisualDescendants().OfType<ArtImage>().Where(image => image.Kind == ArtKind.Rank).ToList();

        Assert.Equal(2, Badges().Count);
        Assert.All(Badges(), badge => Assert.False(badge.IsVisible));

        var ranks = ui.ViewModel.HeroItems.Ranks;
        RankArtTests.SaveBadge(Path.Combine(ui.Art.FolderOf(ArtKind.Rank), RankBucket.ArtId(ranks[0].FirstTier) + ".png"), 40, 32);
        RankArtTests.SaveBadge(Path.Combine(ui.Art.FolderOf(ArtKind.Rank), RankBucket.ArtId(ranks[^1].LastTier) + ".png"), 40, 32);
        ui.Art.Refresh();
        ArtHost.SetRevision(ui.Window, ArtHost.GetRevision(ui.Window) + 1);
        UiHarness.Settle();

        // From shows the badge of the first rank in its group, and To the last in its own.
        Assert.Equal(["01", "11"], Badges().Select(badge => badge.ArtId));
        Assert.All(Badges(), badge => Assert.True(badge.IsVisible));
        Assert.All(Badges(), badge => Assert.Equal(24, badge.Bounds.Width));
        ui.Screenshot("hero_items_ranks.png");
    }

    [AvaloniaFact]
    public async Task AboveAndBelowAverageWinRatesAreTintedBehindTheirNumbers()
    {
        using var ui = new UiHarness(settings => settings.Current.LastPage = 1);
        await SyntheticItemStatsApi.DownloadAsync(ui.Data.Store);
        ui.Data.NotifyReplaced();
        ui.Show();

        var chips = Rows(ui).Select(row => (Row: (HeroItemRowViewModel)row.DataContext!,
            Chip: row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("winRate")).GetVisualAncestors().OfType<Border>().First()))
            .ToList();

        Assert.Contains(chips, pair => pair.Row.AboveAverage);
        Assert.Contains(chips, pair => pair.Row.BelowAverage);
        Assert.All(chips, pair =>
        {
            var tinted = pair.Chip.Background is ISolidColorBrush { Color.A: > 0 };
            Assert.Equal(pair.Row.AboveAverage || pair.Row.BelowAverage, tinted);
        });
    }

    [AvaloniaFact]
    public async Task RightClickingATierShowsOnlyIt()
    {
        using var ui = new UiHarness(settings => settings.Current.LastPage = 1);
        await SyntheticItemStatsApi.DownloadAsync(ui.Data.Store);
        ui.Data.NotifyReplaced();
        ui.Show();
        var page = ui.ViewModel.HeroItems;
        var toggles = ui.Window.HeroItemsPage.GetVisualDescendants().OfType<ToggleButton>().Where(toggle => toggle.DataContext is TierToggle).ToList();
        Assert.Equal(4, toggles.Count);

        var at = toggles[1].TranslatePoint(new Point(toggles[1].Bounds.Width / 2, toggles[1].Bounds.Height / 2), ui.Window)!.Value;
        ui.Window.MouseDown(at, MouseButton.Right);
        ui.Window.MouseUp(at, MouseButton.Right);
        UiHarness.Settle();

        Assert.Equal([false, true, false, false], page.Tiers.Select(tier => tier.IsChecked));
        Assert.Equal([false, true, false, false], toggles.Select(toggle => toggle.IsChecked));
    }

    /// <summary>Ctrl+F belongs to the page in view: here the hero picker, not a jump to the Match page's search.</summary>
    [AvaloniaFact]
    public async Task CtrlFOpensTheHeroSearchAndStaysOnThePage()
    {
        using var ui = new UiHarness(settings => settings.Current.LastPage = 1);
        await SyntheticItemStatsApi.DownloadAsync(ui.Data.Store);
        ui.Data.NotifyReplaced();
        ui.Show();
        var picker = ui.Window.HeroItemsPage.GetVisualDescendants().OfType<SearchComboBox>().Single(box => box.Name == "HeroPicker");
        Assert.False(picker.IsDropDownOpen);

        ui.Window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        UiHarness.Settle();

        Assert.True(ui.ViewModel.IsHeroItemsPage);
        Assert.True(picker.IsDropDownOpen);
        ui.Screenshot("hero_items_search.png");
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
