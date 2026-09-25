using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match.Results;

namespace DeadlockAdvisor.Tests.Ui;

public class MatchPageTests
{
    /// <summary>The line-up the Python app's screenshots use (scratchpad/shoot_pages.py there).</summary>
    private static void SetUpMatch(UiHarness ui)
    {
        var board = ui.ViewModel.Match.Board;
        foreach (var hero in new[] { "haze", "infernus", "vindicta", "abrams", "lash", "seven" })
            board.SetRole(hero, Role.Enemy);
        foreach (var hero in new[] { "dynamo", "kelvin", "paradox", "shiv", "yamato" })
            board.SetRole(hero, Role.Ally);
        board.SetRole("wraith", Role.Self);
        board.ToggleLane("kelvin");
        board.ToggleLane("haze");
        board.ToggleLane("infernus");
    }

    [AvaloniaTheory]
    [InlineData(2, "match_100.png")]
    [InlineData(5, "match_150.png")]
    public void MatchPageRendersAtEachZoom(int zoomIndex, string file)
    {
        using var ui = new UiHarness(settings => settings.Current.ZoomIndex = zoomIndex);
        SetUpMatch(ui);
        ui.ViewModel.Match.ResultsTab = 1;
        ui.Show();

        var full = ui.ViewModel.Match.FullResults;
        var first = full.Entries.OfType<ResultRowViewModel>().First();
        full.Select(first);

        Assert.True(File.Exists(ui.Screenshot(file)));
        Assert.True(ui.ViewModel.Match.Explain.HasItem);
        Assert.NotEmpty(ui.ViewModel.Match.Explain.Contributions);
    }

    [AvaloniaFact]
    public void TieredLaneViewAndEmptyStateRender()
    {
        using var ui = new UiHarness(settings => settings.Current.ResultsByTier = true);
        ui.Show();
        ui.Screenshot("match_empty.png");
        Assert.True(ui.ViewModel.Match.LaneResults.IsEmpty);

        SetUpMatch(ui);
        ui.Screenshot("match_lane_tiered.png");
        Assert.Contains(ui.ViewModel.Match.LaneResults.Entries, entry => entry is TierHeaderViewModel);
    }

    [AvaloniaFact]
    public void TypingAHeroAndPressingEnterAssignsIt()
    {
        using var ui = new UiHarness();
        ui.Show();
        var match = ui.ViewModel.Match;

        match.FocusSearch();
        UiHarness.Settle();
        ui.Window.KeyTextInput("haz");
        UiHarness.Settle();
        Assert.Equal("haz", match.Board.SearchText);
        Assert.True(match.Board.Tiles.Single(t => t.HeroId == "haze").IsHighlighted);
        ui.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        UiHarness.Settle();

        Assert.Equal(Role.Enemy, match.Match.RoleOf("haze"));
        Assert.Equal("", match.Board.SearchText);
        Assert.Equal(["haze"], ui.Settings.Current.LastMatch!.Roles.Keys);
    }

    [AvaloniaFact]
    public void HoveringAnItemIconShowsItsCardAfterTheDelay()
    {
        using var ui = new UiHarness();
        SetUpMatch(ui);
        ui.ViewModel.Match.ResultsTab = 1;
        ui.Show();

        var icon = ui.Window.GetVisualDescendants().OfType<Controls.Art.ArtImage>()
            .First(image => Features.Shared.ItemCard.ItemCardHover.GetItemId(image) is not null && image.IsEffectivelyVisible);
        var itemId = Features.Shared.ItemCard.ItemCardHover.GetItemId(icon);
        var center = icon.TranslatePoint(new Point(17, 17), ui.Window)!.Value;

        ui.Window.MouseMove(center);
        UiHarness.Settle();
        Assert.Null(ui.Window.ItemCards.ShownItemId);

        Thread.Sleep(Features.Shared.ItemCard.ItemCardPresenter.ShowDelay + TimeSpan.FromMilliseconds(100));
        UiHarness.Settle();
        Assert.Equal(itemId, ui.Window.ItemCards.ShownItemId);

        ui.Window.MouseDown(center, MouseButton.Left);
        UiHarness.Settle();
        Assert.Null(ui.Window.ItemCards.ShownItemId);
    }

    [AvaloniaFact]
    public void CtrlTabCyclesPagesAndZoomKeysStep()
    {
        using var ui = new UiHarness();
        ui.Show();

        ui.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
        Assert.Equal(1, ui.ViewModel.CurrentPage);
        ui.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal(0, ui.ViewModel.CurrentPage);

        ui.Window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.Control);
        Assert.Equal(1.15, ui.ViewModel.UiScale);
        ui.Window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.Control);
        Assert.Equal(1.0, ui.ViewModel.UiScale);
    }
}
