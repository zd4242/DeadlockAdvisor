using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;

namespace DeadlockAdvisor.Tests.Ui;

/// <summary>The match page while every hero is on the bar but which one is you isn't known: the question, the preview, the click.</summary>
public class PickSelfTests
{
    private static readonly string[] _heroes =
        ["haze", "infernus", "vindicta", "abrams", "lash", "seven", "wraith", "dynamo", "kelvin", "paradox", "shiv", "yamato"];

    private static void SetUpUnsided(UiHarness ui, string? likely = null)
    {
        var page = ui.ViewModel.Match;
        for (var slot = 0; slot < _heroes.Length; slot++)
            page.Match.PlaceUnsided(_heroes[slot], slot);
        page.Match.SuggestSelf(likely);
        var at = new DateTimeOffset(2026, 9, 26, 20, 0, 0, TimeSpan.Zero);
        page.Match.NetWorth.Add(new NetWorthSnapshot(at, _heroes.Select((hero, slot) => (hero, souls: 14_000 + slot * 1_000)).ToDictionary(entry => entry.hero, entry => entry.souls)));
        page.Board.Refresh();
        page.Refresh();
    }

    private static Point Center(Visual target, Visual root) =>
        target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root)!.Value;

    private static Point Slot(UiHarness ui, string row, int index)
    {
        var items = ui.Window.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Name == row);
        return Center(items.GetVisualDescendants().OfType<RosterSlot>().ElementAt(index), ui.Window);
    }

    private static void Click(Window window, Point at)
    {
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        UiHarness.Settle();
    }

    [AvaloniaFact]
    public void TheBarAsksWhichHeroIsYouAndTheResultsWaitForTheAnswer()
    {
        using var ui = new UiHarness();
        SetUpUnsided(ui);
        ui.Show();

        Assert.True(File.Exists(ui.Screenshot("pick_self_100.png")));
        var page = ui.Window.MatchPage;
        var texts = page.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToList();
        Assert.Contains(Features.Match.Board.MatchBoardViewModel.PickSelfPrompt, texts);
        Assert.Contains("Which hero are you?", texts);
        Assert.DoesNotContain("ALLIES", texts);
        var panel = page.GetVisualDescendants().OfType<StackPanel>().Single(stack => stack.Name == "PickSelf");
        Assert.True(panel.IsEffectivelyVisible);
        Assert.DoesNotContain(page.GetVisualDescendants().OfType<Features.Match.Results.ResultsView>(), results => results.IsEffectivelyVisible);
        Assert.False(page.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ItemSearchToggle").IsEnabled);
    }

    [AvaloniaFact]
    public void AHeroTheStripPointedAtIsTaggedAndNamedInTheQuestion()
    {
        using var ui = new UiHarness();
        SetUpUnsided(ui, likely: "kelvin");
        ui.Show();

        Assert.True(File.Exists(ui.Screenshot("pick_self_likely.png")));
        var texts = ui.Window.MatchPage.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text);
        Assert.Contains("Probably Kelvin.\nClick them to confirm, or click your hero.", texts);
        Assert.True(ui.ViewModel.Match.Board.EnemySlots[2].IsLikelySelf);
    }

    [AvaloniaFact]
    public void HoveringAHeroTintsBothSidesAndAClickOnTheRightPicksThemAsYou()
    {
        using var ui = new UiHarness();
        SetUpUnsided(ui);
        ui.Show();
        var page = ui.ViewModel.Match;
        var board = page.Board;
        var kelvin = Slot(ui, "EnemyRow", 2);

        ui.Window.MouseMove(kelvin);
        UiHarness.Settle();

        Assert.Equal(Role.Self, board.EnemySlots[2].Preview);
        Assert.All(board.AllySlots, slot => Assert.Equal(Role.Enemy, slot.Preview));
        Assert.True(File.Exists(ui.Screenshot("pick_self_hover.png")));

        ui.Window.MouseMove(new Point(2, 2));
        UiHarness.Settle();
        Assert.All(board.AllySlots.Concat(board.EnemySlots), slot => Assert.Equal(Role.None, slot.Preview));

        // The right-hand row is "enemy" on a normal bar, where a click focuses; here it is only a hero to pick.
        Click(ui.Window, kelvin);

        Assert.Equal("kelvin", page.Match.SelfHero);
        Assert.False(board.HasFocus);
        Assert.False(page.NeedsSelf);
        Assert.Equal(_heroes[6..], board.AllySlots.Select(slot => slot.HeroId));
        Assert.True(board.AllySlots[2].IsSelf);
        Assert.False(page.Results.IsEmpty);
        Assert.True(File.Exists(ui.Screenshot("pick_self_picked.png")));
    }

    [AvaloniaTheory]
    [InlineData(2, "100")]
    [InlineData(5, "150")]
    public void TheQuestionFitsTheSmallestWindow(int zoomIndex, string percent)
    {
        using var ui = new UiHarness(settings => settings.Current.ZoomIndex = zoomIndex);
        SetUpUnsided(ui, likely: "kelvin");
        ui.Show();

        ui.Window.Width = ui.Window.MinWidth;
        ui.Window.Height = ui.Window.MinHeight;
        UiHarness.Settle();

        Assert.True(File.Exists(ui.Screenshot($"layout_pick_self_{percent}.png")));
        Assert.Empty(LayoutTests.Overflowing(ui.Window));
    }
}
