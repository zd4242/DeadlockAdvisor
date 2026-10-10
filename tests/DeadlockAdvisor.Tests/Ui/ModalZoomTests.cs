using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.MainWindow.ModelUpdate;
using DeadlockAdvisor.Features.Shared.Modals.Base;
using DeadlockAdvisor.Features.Shared.Modals.Confirmation;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace DeadlockAdvisor.Tests.Ui;

public class ModalZoomTests
{
    private const string Prompt = "Replace the formulas you changed with the published ones? Your edits to these files are kept in the backups folder.";

    private static Border Card(ModalWindow modal) =>
        modal.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "ModalBorder");

    private static Rect CardIn(ModalWindow modal)
    {
        var card = Card(modal);
        var topLeft = card.TranslatePoint(default, modal)!.Value;
        var bottomRight = card.TranslatePoint(new Point(card.Bounds.Width, card.Bounds.Height), modal)!.Value;
        return new Rect(topLeft, bottomRight);
    }

    private static async Task<ModalWindow> ShowConfirmAsync(UiHarness ui)
    {
        ui.Show();
        ui.Services.GetRequiredService<IModalService>().Confirm(Prompt, "Replace", () => { });
        UiHarness.Settle();
        var modal = ui.Window.OwnedWindows.OfType<ModalWindow>().Single();
        Assert.True(await UiHarness.WaitUntilAsync(() => Card(modal).Opacity >= 1));
        return modal;
    }

    // Layout rounds the card to whole pixels before and after the zoom.
    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - 2, expected + 2);

    [AvaloniaTheory]
    [InlineData(2, "modal_100.png")]
    [InlineData(5, "modal_150.png")]
    [InlineData(7, "modal_200.png")]
    public async Task ADialogIsAsLargeAsTheAppAtEachZoom(int zoomIndex, string file)
    {
        using var unzoomed = new UiHarness();
        var normal = CardIn(await ShowConfirmAsync(unzoomed));

        using var ui = new UiHarness(settings => settings.Current.ZoomIndex = zoomIndex);
        var card = CardIn(await ShowConfirmAsync(ui));
        Assert.True(File.Exists(ui.ScreenshotModal(file)));

        var scale = ZoomLevels.Steps[zoomIndex];
        Near(normal.Width * scale, card.Width);
        Near(normal.Height * scale, card.Height);
        Near(ui.Window.ClientSize.Width / 2, card.Center.X);
        Near(ui.Window.ClientSize.Height / 2, card.Center.Y);
    }

    [AvaloniaFact]
    public async Task ZoomingWhileADialogIsOpenResizesIt()
    {
        using var ui = new UiHarness();
        var modal = await ShowConfirmAsync(ui);
        var before = CardIn(modal);

        ui.Settings.Update(settings => settings.ZoomIndex = 5);
        UiHarness.Settle();
        Near(before.Width * 1.5, CardIn(modal).Width);

        ui.Settings.Update(settings => settings.ZoomIndex = ZoomLevels.DefaultIndex);
        UiHarness.Settle();
        Near(before.Width, CardIn(modal).Width);
    }

    [AvaloniaFact]
    public async Task ADialogNeverOutgrowsTheWindowAtTheHighestZoom()
    {
        using var ui = new UiHarness(settings => settings.Current.ZoomIndex = ZoomLevels.Steps.Count - 1);
        ui.Window.Width = 800;
        ui.Window.Height = 500;
        ui.Show();
        var plan = new ModelUpdatePlan(new ModelManifest(ModelManifest.CurrentFormat, "2026-10-09", new Dictionary<string, string>(), new Dictionary<string, string>())
            {
                Notes = [new("2026-10-09", "Spirit items rate higher against heroes who heal."), new("2026-10-02", "Rated the new heroes.")],
            },
            null, new Dictionary<string, string?>(), [DataStore.ItemsFile, DataStore.ItemStatsFile], [DataStore.HeroScoresFile, DataStore.ItemCoefficientsFile]);
        var modals = ui.Services.GetRequiredService<IModalService>();

        modals.ShowModal(ModelUpdateViewModel.Update(modals, plan, _ => { }));
        UiHarness.Settle();

        var modal = ui.Window.OwnedWindows.OfType<ModalWindow>().Single();
        var view = modal.GetVisualDescendants().OfType<ModelUpdateView>().Single();
        Assert.True(await UiHarness.WaitUntilAsync(() => view.GetVisualAncestors().All(visual => visual.Opacity >= 1)));
        var card = CardIn(modal);
        Assert.InRange(card.Width, 0, ui.Window.ClientSize.Width);
        Assert.InRange(card.Height, 0, ui.Window.ClientSize.Height);
        Assert.True(card.Left >= -0.5 && card.Top >= -0.5);
        ui.ScreenshotModal("modal_200_small_window.png");
    }
}
