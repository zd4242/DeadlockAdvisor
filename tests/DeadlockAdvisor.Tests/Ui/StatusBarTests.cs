using System.Reactive.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.MainWindow;
using DeadlockAdvisor.Features.MainWindow.MatchDownload;
using DeadlockAdvisor.Features.Shared.Modals.Base;
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
        using var ui = new UiHarness(settings => settings.Current.WelcomeOffered = true);
        ui.Api.Json[MatchStatsService.Patches] = () => JsonNode.Parse("""[{"title": "10-01-2026 Gameplay Update"}]""");

        ui.Show();

        var status = ui.ViewModel.DataStatus;
        Assert.True(status.IsOutdated);
        Assert.EndsWith("· 10-01 is out", status.Label);
        Assert.StartsWith("Patch 10-01 is out since these were fetched.", status.Warning);
        OpenCard(ui);
        ui.Screenshot("status_newer_patch.png");
    }

    [AvaloniaFact]
    public async Task ANewerVersionShowsAsAChipThatDismissingRemoves()
    {
        var github = new FakeDeadlockApi();
        github.Bytes[AppUpdateService.LatestUrl] =
            """{"tag_name": "v0.2.0", "html_url": "https://github.com/zd4242/DeadlockAdvisor/releases/tag/v0.2.0"}"""u8.ToArray();
        using var ui = new UiHarness(settings => settings.Current.WelcomeOffered = true,
            services => services.AddSingleton<IAppUpdateService>(new AppUpdateService(github, new Version(0, 1, 1))));
        ui.Show();

        var chip = ui.Window.StatusBar.GetVisualDescendants().OfType<AppUpdateView>().Single();
        Assert.True(await UiHarness.WaitUntilAsync(() => chip.GetVisualDescendants().OfType<Border>().First().IsVisible));
        var barHeight = ui.Window.StatusBar.Bounds.Height;
        Assert.Contains("Version 0.2.0 is out", chip.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
        ui.Screenshot("status_app_update.png");

        chip.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "Dismiss").Command!.Execute(null);
        UiHarness.Settle();

        Assert.False(chip.GetVisualDescendants().OfType<Border>().First().IsVisible);
        Assert.Equal("0.2.0", ui.Settings.Current.SkippedAppVersion);
        Assert.Equal(barHeight, ui.Window.StatusBar.Bounds.Height);
    }

    /// <summary>The chip opens its card when the pointer rests on it, not as it passes over.</summary>
    [AvaloniaFact]
    public async Task RestingOnTheMatchDataChipOpensItsCard()
    {
        using var ui = new UiHarness(settings => settings.Current.WelcomeOffered = true);
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
        Assert.Contains("Download again…", card.GetLogicalDescendants().OfType<Button>().Select(button => button.Content as string));
        ui.Screenshot("status_card.png");
    }

    [AvaloniaFact]
    public async Task DownloadsShowInTheStatusBarAndHoldTheWindowOpen()
    {
        var matchStats = new HeldMatchStats();
        var art = new HeldArtDownload();
        using var ui = new UiHarness(settings => settings.Current.WelcomeOffered = true, services =>
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
        _ = menu.DownloadMatchDataAsync(HeldMatchStats.Plan);
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
    public void ClickingTheRunningMatchDataChipShowsEachPhase()
    {
        var matchStats = new HeldMatchStats();
        using var ui = new UiHarness(settings => settings.Current.WelcomeOffered = true,
            services => services.AddSingleton<IMatchStatsService>(matchStats));
        ui.Show();

        _ = ui.ViewModel.DataMenu.DownloadMatchDataAsync(HeldMatchStats.Plan);
        matchStats.Progress!.Report(new MatchFetchProgress(1, 120, 400, 200, 480, "09-29 · Emissary – Oracle · Enemies: Haze", 3_100_000));
        UiHarness.Settle();
        var running = ui.Window.StatusBar.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "Running");
        running.Flyout!.ShowAt(running);
        UiHarness.Settle();

        var card = ((Flyout)running.Flyout).Content as Control;
        var texts = card!.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains("09-29 · every match", texts);
        Assert.Contains("in use", texts);
        Assert.Contains("Enemies: Haze", texts);
        Assert.Contains("200 of 480 calls", texts);
        ui.Screenshot("status_download_details.png");
    }

    [AvaloniaFact]
    public async Task TheDownloadDialogSaysWhatItFetchesAndAboutHowLong()
    {
        var matchStats = new HeldMatchStats();
        using var ui = new UiHarness(settings => settings.Current.WelcomeOffered = true,
            services => services.AddSingleton<IMatchStatsService>(matchStats));
        ui.Show();

        await ui.ViewModel.DataMenu.DownloadMatchDataCommand.Execute();
        UiHarness.Settle();

        var modal = ui.Window.OwnedWindows.OfType<ModalWindow>().Single();
        var dialog = modal.GetVisualDescendants().OfType<MatchDownloadView>().Single();
        // Faded in, for the screenshot.
        Assert.True(await UiHarness.WaitUntilAsync(() => dialog.GetVisualAncestors().All(visual => visual.Opacity >= 1)));
        var texts = dialog.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains("No match data yet.", texts);
        Assert.Contains("about 30 s · 1.2 MB", texts);
        Assert.Contains("Patch 09-29", texts);
        Assert.Contains("New: downloading it", texts);
        Assert.Contains("Up to date: skipped", texts);
        ui.ScreenshotModal("match_download_dialog.png");
    }

    [AvaloniaFact]
    public async Task TheSharedDownloadsDialogHasNoChoiceToMake()
    {
        using var data = Support.Golden.CopyData();
        var store = DataStore.Load(data.Path);
        await SyntheticItemStatsApi.DownloadAsync(store);
        using var ui = new UiHarness(settings => settings.Current.WelcomeOffered = true);
        var fetched = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds();
        var entries = new List<SnapshotPatch>();
        foreach (var segment in store.MatchSegments.Select(segment => segment.Ended ? segment : segment with { Until = fetched, FetchedAt = fetched }))
        {
            var (entry, gzipped) = MatchSnapshot.Pack(segment);
            ui.Api.Bytes[MatchSnapshot.UrlOf(entry)] = gzipped;
            entries.Add(entry);
        }
        ui.Api.Bytes[MatchSnapshot.ManifestUrl] = new MatchSnapshot(fetched, entries).ToJsonBytes();
        ui.Show();

        await ui.ViewModel.DataMenu.DownloadMatchDataCommand.Execute();
        UiHarness.Settle();

        var modal = ui.Window.OwnedWindows.OfType<ModalWindow>().Single();
        var dialog = modal.GetVisualDescendants().OfType<MatchDownloadView>().Single();
        Assert.True(await UiHarness.WaitUntilAsync(() => dialog.GetVisualAncestors().All(visual => visual.Opacity >= 1)));
        var shown = dialog.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToList();
        Assert.Contains("All matches + rank groups", shown);
        Assert.DoesNotContain("All matches", shown);
        Assert.Equal(2, shown.Count(text => text == "New: downloading it, with rank groups"));
        ui.ScreenshotModal("match_download_shared.png");
    }

    [AvaloniaFact]
    public void AnOfflinePatchCheckLeavesTheStatusAlone()
    {
        using var ui = new UiHarness(settings => settings.Current.WelcomeOffered = true);

        ui.Show();

        Assert.False(ui.ViewModel.DataStatus.IsOutdated);
        Assert.Null(ui.ViewModel.DataStatus.Warning);
        Assert.StartsWith("Match data · patch 09-16 · ", ui.ViewModel.DataStatus.Label);
    }

    [AvaloniaFact]
    public void WithoutMatchDataTheCardOffersToFetchIt()
    {
        using var ui = new UiHarness(settings => settings.Current.WelcomeOffered = true);
        ui.Data.Store.MatchMeta.Clear();
        ui.Data.NotifyReplaced();
        ui.Show();

        var status = ui.ViewModel.DataStatus;
        Assert.False(status.HasData);
        Assert.Equal("No match data", status.Label);
        Assert.Empty(status.Facts);
        Assert.Equal("Download Match Data…", status.FetchText);
        Assert.Same(ui.ViewModel.DataMenu.DownloadMatchDataCommand, status.FetchCommand);
        OpenCard(ui);
        ui.Screenshot("status_card_no_data.png");
    }

    /// <summary>How much of the model is filled in, which only someone filling it in needs.</summary>
    [AvaloniaFact]
    public void CoverageShowsOnlyWithTheEditors()
    {
        using var ui = new UiHarness(settings => settings.Current.WelcomeOffered = true);
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
        using var ui = new UiHarness(settings => settings.Current.WelcomeOffered = true);
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
