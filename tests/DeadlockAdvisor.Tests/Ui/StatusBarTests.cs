using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Shared.BackgroundJobs;
using DeadlockAdvisor.Features.Shared.Modals.Confirmation;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DeadlockAdvisor.Tests.Ui;

public class StatusBarTests
{
    [AvaloniaFact]
    public void MatchDataTurnsRedWhenANewerPatchIsOut()
    {
        using var ui = new UiHarness(settings => settings.Current.ArtDownloadOffered = true);
        ui.Api.Json[MatchStatsService.Patches] = () => JsonNode.Parse("""[{"title": "10-01-2026 Gameplay Update"}]""");

        ui.Show();

        var status = ui.ViewModel.DataStatus;
        Assert.True(status.IsOutdated);
        Assert.EndsWith("· 10-01 is out", status.Label);
        Assert.StartsWith("Patch 10-01 is out since these were fetched.", status.Warning);
        OpenCard(ui);
        ui.Screenshot("status_newer_patch.png");
    }

    /// <summary>The chip opens its card when the pointer rests on it, not as it passes over.</summary>
    [AvaloniaFact]
    public async Task RestingOnTheMatchDataChipOpensItsCard()
    {
        using var ui = new UiHarness(settings => settings.Current.ArtDownloadOffered = true);
        ui.Show();
        var chip = Chip(ui);
        var center = chip.TranslatePoint(new Point(chip.Bounds.Width / 2, chip.Bounds.Height / 2), ui.Window)!.Value;

        ui.Window.MouseMove(center);
        UiHarness.Settle();
        Assert.False(chip.Flyout!.IsOpen);

        Assert.True(await UiHarness.WaitUntilAsync(() => chip.Flyout.IsOpen));
        var card = (Control)((Flyout)chip.Flyout).Content!;
        var facts = card.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains("Every match, ranked or not", facts);
        Assert.Contains("Fetch again", card.GetLogicalDescendants().OfType<Button>().Select(button => button.Content as string));
        ui.Screenshot("status_card.png");
    }

    [AvaloniaFact]
    public async Task DownloadsShowInTheStatusBarAndHoldTheWindowOpen()
    {
        var matchStats = new HeldMatchStats();
        var art = new HeldArtDownload();
        using var ui = new UiHarness(settings => settings.Current.ArtDownloadOffered = true, services =>
        {
            services.AddSingleton<IMatchStatsService>(matchStats);
            services.AddSingleton<IArtDownloadService>(art);
        });
        var shown = new List<ViewModelBase>();
        using var watchModals = ui.Services.GetRequiredService<IModalService>().ShowModalObservable.Subscribe(shown.Add);
        var closed = false;
        ui.Window.Closed += (_, _) => closed = true;
        ui.Show();
        var barHeight = ui.Window.StatusBar.Bounds.Height;

        var menu = ui.ViewModel.DataMenu;
        menu.FetchMatchStatsCommand.Execute().Subscribe();
        matchStats.Progress!.Report(new MatchFetchProgress(2, 52, 400, 812, 2600, "09-29 · Emissary – Oracle · Enemies: Haze", 0));
        var artRun = menu.DownloadArtAsync(force: false);
        art.Finish(new ArtDownloadReport([new ArtGroupReport("Hero portraits", 38, 38, 38, 0, [], [])]));
        await artRun;
        UiHarness.Settle();

        var chips = ui.Window.StatusBar.GetVisualDescendants().OfType<BackgroundJobView>().ToList();
        Assert.Equal(["Match data", "Art"], chips.Select(chip => ((BackgroundJobViewModel)chip.DataContext!).Title));
        Assert.Equal(barHeight, ui.Window.StatusBar.Bounds.Height);
        ui.Screenshot("status_downloads.png");

        ui.Window.Close();
        Assert.False(closed);
        var ask = Assert.IsType<ConfirmationModalViewModel>(Assert.Single(shown));
        Assert.StartsWith("Still downloading:\n  • Match data: 31%", ask.Prompt);
        ask.ConfirmCommand!.Execute(null);
        UiHarness.Settle();
        Assert.True(closed);
    }

    [AvaloniaFact]
    public void AnOfflinePatchCheckLeavesTheStatusAlone()
    {
        using var ui = new UiHarness(settings => settings.Current.ArtDownloadOffered = true);

        ui.Show();

        Assert.False(ui.ViewModel.DataStatus.IsOutdated);
        Assert.Null(ui.ViewModel.DataStatus.Warning);
        Assert.StartsWith("Match data · patch 09-16 · ", ui.ViewModel.DataStatus.Label);
    }

    [AvaloniaFact]
    public void WithoutMatchDataTheCardOffersToFetchIt()
    {
        using var ui = new UiHarness(settings => settings.Current.ArtDownloadOffered = true);
        ui.Data.Store.MatchMeta.Clear();
        ui.Data.NotifyReplaced();
        ui.Show();

        var status = ui.ViewModel.DataStatus;
        Assert.False(status.HasData);
        Assert.Equal("No match data", status.Label);
        Assert.Empty(status.Facts);
        Assert.Equal("Fetch Match Stats", status.FetchText);
        Assert.Same(ui.ViewModel.DataMenu.FetchMatchStatsCommand, status.FetchCommand);
        OpenCard(ui);
        ui.Screenshot("status_card_no_data.png");
    }

    /// <summary>How much of the model is filled in, which only someone filling it in needs.</summary>
    [AvaloniaFact]
    public void CoverageShowsOnlyWithTheEditors()
    {
        using var ui = new UiHarness(settings => settings.Current.ArtDownloadOffered = true);
        ui.Show();
        Assert.False(ui.ViewModel.DataStatus.ShowsCoverage);
        Assert.Empty(ui.ViewModel.DataStatus.Coverage);

        ui.ViewModel.Settings.General.ShowModelEditors = true;

        Assert.True(ui.ViewModel.DataStatus.ShowsCoverage);
        Assert.Equal(["Hero traits rated", "Items tagged", "Formula rules"], ui.ViewModel.DataStatus.Coverage.Select(fact => fact.Label));
        OpenCard(ui);
        ui.Screenshot("status_card_editors.png");
    }

    [AvaloniaFact]
    public void TheZoomOnlyShowsWhileItsOffItsDefault()
    {
        using var ui = new UiHarness(settings => settings.Current.ArtDownloadOffered = true);
        ui.Show();
        Assert.Equal("", ui.ViewModel.ZoomText);

        ui.ViewModel.ZoomInCommand.Execute().Subscribe();
        Assert.Equal("115%", ui.ViewModel.ZoomText);
        ui.ViewModel.ResetZoomCommand.Execute().Subscribe();
        Assert.Equal("", ui.ViewModel.ZoomText);
    }

    private static Button Chip(UiHarness ui) =>
        ui.Window.StatusBar.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "Chip");

    private static void OpenCard(UiHarness ui)
    {
        var chip = Chip(ui);
        chip.Flyout!.ShowAt(chip);
        UiHarness.Settle();
    }
}
