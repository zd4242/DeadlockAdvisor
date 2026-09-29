using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
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
        // The laning capture has two uncertain reads.
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
        Assert.Contains(review.Slots, slot => slot.IsUncertain);
        UiHarness.Settle();
        // The modal is a window of its own: its portraits need the art service handed on to it.
        var portraits = ui.Window.OwnedWindows.OfType<ModalWindow>().Single().GetVisualDescendants().OfType<ArtImage>().ToList();
        Assert.NotEmpty(portraits);
        Assert.All(portraits, portrait => Assert.NotNull(ArtHost.GetService(portrait)));
        ui.ScreenshotModal("detect_review.png");

        var corrected = review.Slots[3];
        corrected.SelectedHero = corrected.Choices.Single(choice => choice.HeroId == "haze");
        Assert.Equal("corrected from Mirage", corrected.Detail);
        review.ToggleSelf(1);
        ui.ScreenshotModal("detect_review_no_self.png");
    }
}
