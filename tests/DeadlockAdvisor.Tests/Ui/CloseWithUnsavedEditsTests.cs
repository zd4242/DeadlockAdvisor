using Avalonia.Headless.XUnit;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Shared.Modals.Confirmation;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace DeadlockAdvisor.Tests.Ui;

/// <summary>Closing with edits the disk wouldn't take asks first, instead of losing them.</summary>
public class CloseWithUnsavedEditsTests
{
    private static FileStream Lock(UiHarness ui) =>
        new(Path.Combine(ui.Data.DataDir, DataStore.HeroScoresFile), FileMode.Open, FileAccess.Read, FileShare.None);

    private static (List<ViewModelBase> Shown, IDisposable Watch) WatchModals(UiHarness ui)
    {
        var shown = new List<ViewModelBase>();
        return (shown, ui.Services.GetRequiredService<IModalService>().ShowModalObservable.Subscribe(shown.Add));
    }

    [AvaloniaFact]
    public void ClosingAfterAFailedSaveAsksAndKeepingTheAppOpenStays()
    {
        using var ui = new UiHarness();
        var (shown, watch) = WatchModals(ui);
        using var _ = watch;
        var closed = false;
        ui.Window.Closed += (_, _) => closed = true;
        ui.Show();
        ui.Data.MarkEdited(DataFiles.HeroScores);

        using (Lock(ui))
        {
            ui.Window.Close();
            UiHarness.Settle();

            Assert.False(closed);
            var ask = Assert.IsType<ConfirmationModalViewModel>(Assert.Single(shown));
            Assert.Contains("Your latest edits couldn't be saved", ask.Prompt);
            Assert.Equal("Close anyway", ask.ConfirmText);
            Assert.Equal("Keep the app open", ask.CancelText);

            ask.CancelCommand!.Execute(null);
            UiHarness.Settle();
            Assert.False(closed);
            Assert.False(ui.Services.GetRequiredService<IModalService>().IsModalOpen);
        }
    }

    [AvaloniaFact]
    public void CloseAnywayClosesAndLeavesTheEditsUnsaved()
    {
        using var ui = new UiHarness();
        var (shown, watch) = WatchModals(ui);
        using var _ = watch;
        var closed = false;
        ui.Window.Closed += (_, _) => closed = true;
        ui.Show();
        ui.Data.MarkEdited(DataFiles.HeroScores);

        using (Lock(ui))
        {
            ui.Window.Close();
            var ask = Assert.IsType<ConfirmationModalViewModel>(Assert.Single(shown));
            ask.ConfirmCommand!.Execute(null);
            UiHarness.Settle();

            Assert.True(closed);
            Assert.Single(shown);
        }
    }

    [AvaloniaFact]
    public void ClosingAfterTheProblemIsFixedJustSavesAndCloses()
    {
        using var ui = new UiHarness();
        var (shown, watch) = WatchModals(ui);
        using var _ = watch;
        var closed = false;
        ui.Window.Closed += (_, _) => closed = true;
        ui.Show();
        ui.Data.Store.SetHeroScore(ui.Data.Store.Heroes.Keys.First(), "max_hp", 77);
        ui.Data.MarkEdited(DataFiles.HeroScores);
        using (Lock(ui))
            Assert.False(ui.Data.FlushSaves());

        ui.Window.Close();
        UiHarness.Settle();

        Assert.True(closed);
        Assert.Empty(shown);
        Assert.Equal(77, DataStore.Load(ui.Data.DataDir).HeroScore(ui.Data.Store.Heroes.Keys.First(), "max_hp"));
    }

    [AvaloniaFact]
    public void AnOsShutdownIsNeverHeldUp()
    {
        using var ui = new UiHarness();
        var (shown, watch) = WatchModals(ui);
        using var _ = watch;
        ui.Show();
        ui.Data.MarkEdited(DataFiles.HeroScores);

        using (Lock(ui))
            Assert.True(ui.ViewModel.OnClosing(osShutdown: true));

        Assert.Empty(shown);
    }
}
