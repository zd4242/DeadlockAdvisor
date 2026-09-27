using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
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

        Assert.True(ui.ViewModel.DataStatusAlert);
        Assert.EndsWith("— patch 10-01 is out, refetch", ui.ViewModel.DataStatusText);
        ui.Screenshot("status_newer_patch.png");
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
        matchStats.Progress!.Report(new FetchProgress(812, 2600, "as/full: Haze · Emissary"));
        var artRun = menu.DownloadArtAsync(force: false);
        art.Finish(new ArtDownloadReport([new ArtGroupReport("Hero portraits", 38, 38, 38, 0, [])]));
        await artRun;
        UiHarness.Settle();

        var chips = ui.Window.StatusBar.GetVisualDescendants().OfType<BackgroundJobView>().ToList();
        Assert.Equal(["Match stats", "Art"], chips.Select(chip => ((BackgroundJobViewModel)chip.DataContext!).Title));
        Assert.Equal(barHeight, ui.Window.StatusBar.Bounds.Height);
        ui.Screenshot("status_downloads.png");

        ui.Window.Close();
        Assert.False(closed);
        var ask = Assert.IsType<ConfirmationModalViewModel>(Assert.Single(shown));
        Assert.StartsWith("Still downloading:\n  • Match stats: 31%", ask.Prompt);
        ask.ConfirmCommand!.Execute(null);
        UiHarness.Settle();
        Assert.True(closed);
    }

    [AvaloniaFact]
    public void AnOfflinePatchCheckLeavesTheStatusAlone()
    {
        using var ui = new UiHarness(settings => settings.Current.ArtDownloadOffered = true);

        ui.Show();

        Assert.False(ui.ViewModel.DataStatusAlert);
        Assert.StartsWith("   ·   match data: patch ", ui.ViewModel.DataStatusText);
    }
}
