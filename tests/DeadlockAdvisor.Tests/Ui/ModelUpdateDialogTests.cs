using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DeadlockAdvisor.Features.MainWindow.ModelUpdate;
using DeadlockAdvisor.Features.Shared.Modals.Base;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace DeadlockAdvisor.Tests.Ui;

public class ModelUpdateDialogTests
{
    [AvaloniaFact]
    public async Task TheDialogListsTheChangedFilesUntickedAndWhatElseUpdates()
    {
        using var ui = new UiHarness();
        ui.Show();
        var plan = new ModelUpdatePlan(new ModelManifest(ModelManifest.CurrentFormat, "2026-10-09", new Dictionary<string, string>(), new Dictionary<string, string>()),
            null, new Dictionary<string, string?>(), [DataStore.ItemsFile, DataStore.ItemStatsFile], [DataStore.HeroScoresFile, DataStore.ItemCoefficientsFile]);
        var modals = ui.Services.GetRequiredService<IModalService>();

        modals.ShowModal(new ModelUpdateViewModel(modals, plan, _ => { }));
        UiHarness.Settle();

        var modal = ui.Window.OwnedWindows.OfType<ModalWindow>().Single();
        var view = modal.GetVisualDescendants().OfType<ModelUpdateView>().Single();
        Assert.True(await UiHarness.WaitUntilAsync(() => view.GetVisualAncestors().All(visual => visual.Opacity >= 1)));
        var boxes = view.GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(2, boxes.Count);
        Assert.All(boxes, box => Assert.False(box.IsChecked));
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains("Hero trait ratings", texts);
        Assert.Contains("Also updated, as you haven't changed them: Items, Item stats.", texts);
        ui.ScreenshotModal("model_update_dialog.png");
    }
}
