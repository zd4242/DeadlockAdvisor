using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using DeadlockAdvisor.Services;

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
    public void AnOfflinePatchCheckLeavesTheStatusAlone()
    {
        using var ui = new UiHarness(settings => settings.Current.ArtDownloadOffered = true);

        ui.Show();

        Assert.False(ui.ViewModel.DataStatusAlert);
        Assert.StartsWith("   ·   match data: patch ", ui.ViewModel.DataStatusText);
    }
}
