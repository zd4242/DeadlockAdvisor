using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Controls.Art;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Match.Detect;
using DeadlockAdvisor.Features.Shared.Modals.Base;
using static DeadlockAdvisor.Tests.Support.VisionData;

namespace DeadlockAdvisor.Tests.Ui;

public class DetectReviewTests
{
    [AvaloniaFact]
    public async Task F9DetectsAndTheReviewRenders()
    {
        using var ui = OpenHarness();
        var review = await OpenReviewAsync(ui);
        Assert.Equal(1, ui.Capture.Captures);
        Assert.Equal(1, review.SelfSlot);
        Assert.DoesNotContain(review.Slots, slot => slot.IsUncertain);
        UiHarness.Settle();
        // The modal is a window of its own: its portraits need the art service handed on to it.
        var portraits = ui.Window.OwnedWindows.OfType<ModalWindow>().Single().GetVisualDescendants().OfType<ArtImage>().ToList();
        Assert.NotEmpty(portraits);
        Assert.All(portraits, portrait => Assert.NotNull(ArtHost.GetService(portrait)));
        ui.ScreenshotModal("detect_review.png");

        // Typing on a slot's dropdown opens it on a search: the best match is picked, the misses hidden, and Enter closes it.
        var corrected = review.Slots[3];
        var modal = ui.Window.OwnedWindows.OfType<ModalWindow>().Single();
        var dropdown = modal.GetVisualDescendants().OfType<SearchComboBox>().Single(box => box.DataContext == corrected);
        var view = modal.GetVisualDescendants().OfType<DetectReviewView>().Single();
        var heightBeforeCorrecting = view.Bounds.Height;
        dropdown.Focus();
        modal.KeyTextInput("h");
        UiHarness.Settle();
        Assert.True(dropdown.IsDropDownOpen);
        // The search keeps the keyboard as each keystroke and arrow key selects a new match.
        var search = Assert.IsType<SearchBox>(modal.FocusManager!.GetFocusedElement());
        var firstMatch = corrected.HeroId;
        modal.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.NotEqual(firstMatch, corrected.HeroId);
        Assert.Same(search, modal.FocusManager.GetFocusedElement());
        modal.KeyTextInput("a");
        UiHarness.Settle();
        modal.KeyTextInput("z");
        UiHarness.Settle();
        Assert.Same(search, modal.FocusManager.GetFocusedElement());
        Assert.Equal("haz", search.Text);
        Assert.Equal("haze", corrected.HeroId);
        Assert.False(dropdown.ContainerFromItem(corrected.Choices.Single(choice => choice.HeroId == "abrams"))!.IsVisible);
        // Keys typed into the search stay unhandled, or Windows drops the characters they type, and Space doesn't
        // pick an entry.
        var keysHandled = new List<bool>();
        modal.AddHandler(InputElement.KeyDownEvent, (_, e) => keysHandled.Add(e.Handled), RoutingStrategies.Bubble, handledEventsToo: true);
        modal.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
        modal.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.Equal([false, false], keysHandled);
        Assert.True(dropdown.IsDropDownOpen);
        ui.ScreenshotModal("detect_review_search.png");
        modal.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.False(dropdown.IsDropDownOpen);
        Assert.Equal("corrected from Mirage", corrected.Detail);
        // The first correction brings in the remember option without growing the dialog, which could tip it into scrolling.
        Assert.True(view.Bounds.Height <= heightBeforeCorrecting, $"{view.Bounds.Height} > {heightBeforeCorrecting}");
        review.ToggleSelf(1);
        ui.ScreenshotModal("detect_review_no_self.png");
    }

    // Every hero in the laning capture reads confidently, which would apply it without a review.
    private static UiHarness OpenHarness(int? zoomIndex = null)
    {
        var ui = new UiHarness(settings =>
        {
            settings.Current.AutoApplyDetect = false;
            if (zoomIndex is { } index)
                settings.Current.ZoomIndex = index;
        });
        CopyTopbarInto(ui.Data.AssetsDir);
        ui.Capture.Next = Capture("laning_2560x1440_band");
        return ui;
    }

    private static async Task<DetectReviewViewModel> OpenReviewAsync(UiHarness ui)
    {
        ui.Show();
        ui.ViewModel.Match.FocusSearch();
        UiHarness.Settle();
        ui.Window.KeyPressQwerty(PhysicalKey.F9, RawInputModifiers.None);

        // Detection takes a few seconds here, but several times longer on a slow CI runner.
        Assert.True(await UiHarness.WaitUntilAsync(() => Review(ui) is not null, TimeSpan.FromSeconds(60)));
        return Review(ui)!;
    }

    private static DetectReviewViewModel? Review(UiHarness ui) =>
        ui.Window.OwnedWindows.OfType<ModalWindow>().SingleOrDefault()?.DataContext is ModalViewModel { Content: DetectReviewViewModel review } ? review : null;

    [AvaloniaTheory]
    [InlineData(2, 700)]
    [InlineData(5, 700)]
    [InlineData(5, 900)]
    [InlineData(7, 700)]
    public async Task TheReviewShrinksToTheWindowRatherThanScrolling(int zoomIndex, int height)
    {
        using var ui = OpenHarness(zoomIndex);
        ui.Window.Width = 1600;
        ui.Window.Height = height;
        await OpenReviewAsync(ui);
        UiHarness.Settle();

        var modal = ui.Window.OwnedWindows.OfType<ModalWindow>().Single();
        var scroller = modal.GetVisualDescendants().OfType<ScrollViewer>().First();
        var card = modal.GetVisualDescendants().OfType<DetectReviewView>().Single();
        Assert.True(scroller.Extent.Height <= scroller.Viewport.Height + 1, $"Scrolls: {scroller.Extent.Height} in {scroller.Viewport.Height}");
        var bounds = card.TranslatePoint(new Point(0, card.Bounds.Height), modal);
        Assert.True(bounds is { } corner && corner.Y <= modal.ClientSize.Height, $"Runs off the window: {bounds}");
        ui.ScreenshotModal($"detect_review_{ZoomLevels.Steps[zoomIndex] * 100:0}_{height}.png");
    }
}
