using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Features.HeroItems;
using DeadlockAdvisor.Tests.Fakes;

namespace DeadlockAdvisor.Tests.Ui;

public class HeroItemsUsageSliderTests
{
    /// <summary>The slider's travel isn't linear in percents, so a thumb between two whole percents is pulled onto the nearer one.</summary>
    [AvaloniaFact]
    public async Task TheUsageSliderSnapsToWholePercentsAndItsKeysStepByThem()
    {
        using var ui = new UiHarness(settings => settings.Current.LastPage = 1);
        await SyntheticItemStatsApi.DownloadAsync(ui.Data.Store);
        ui.Data.NotifyReplaced();
        ui.Show();
        var page = ui.ViewModel.HeroItems;
        var slider = ui.Window.HeroItemsPage.GetVisualDescendants().OfType<Slider>().Single();

        slider.Value = HeroItemsViewModel.UsageToPosition(2) + 1;
        Assert.Equal(2, page.MinUsagePercent);
        Assert.Equal(HeroItemsViewModel.UsageToPosition(2), slider.Value, 6);

        void Press(Key key) => slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Press(Key.Right);
        Assert.Equal(3, page.MinUsagePercent);
        Assert.Equal(HeroItemsViewModel.UsageToPosition(3), slider.Value, 6);
        Press(Key.PageDown);
        Assert.Equal(0, page.MinUsagePercent);
        Press(Key.End);
        Assert.Equal(HeroItemsViewModel.MaxMinUsagePercent, page.MinUsagePercent);
    }
}
