using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Controls.Art;
using DeadlockAdvisor.Features.Match.Detect;
using DeadlockAdvisor.Features.Shared.Modals.Base;
using static DeadlockAdvisor.Tests.Support.VisionData;

namespace DeadlockAdvisor.Tests.Ui;

public class DetectReviewTests
{
    [AvaloniaFact]
    public async Task F9DetectsAndTheReviewRenders()
    {
        using var ui = new UiHarness();
        CopyTopbarInto(ui.Data.AssetsDir);
        ui.Capture.Next = Capture("laning_2560x1440_band");
        ui.Show();

        ui.ViewModel.Match.FocusSearch();
        UiHarness.Settle();
        ui.Window.KeyPressQwerty(PhysicalKey.F9, RawInputModifiers.None);

        DetectReviewViewModel? Review() =>
            ui.Window.OwnedWindows.OfType<ModalWindow>().SingleOrDefault()?.DataContext is ModalViewModel { Content: DetectReviewViewModel review } ? review : null;
        Assert.True(await UiHarness.WaitUntilAsync(() => Review() is not null, TimeSpan.FromSeconds(10)));
        var review = Review()!;
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
}
