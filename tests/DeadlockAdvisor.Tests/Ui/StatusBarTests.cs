using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
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

    /// <summary>An update that installs when told to, after reporting part of its download.</summary>
    private sealed class HeldAppUpdate : IAppUpdateService
    {
        public readonly TaskCompletionSource Finish = new();
        public Version? Current => new(0, 1, 1);
        public bool CanInstall(AppRelease release) => true;
        public void RestartAfterExit(bool restart = true) { }
        public Task CleanUpAsync() => Task.CompletedTask;

        public Task<AppRelease?> LatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AppRelease?>(new("0.2.0", "https://github.com/zd4242/DeadlockAdvisor/releases/tag/v0.2.0",
                new("https://github.com/zd4242/DeadlockAdvisor/releases/download/v0.2.0/DeadlockAdvisor.exe", 100, "")));

        public async Task DownloadAsync(AppRelease release, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken = default)
        {
            progress?.Report(new DownloadProgress(42, 100));
            await Finish.Task;
        }
    }

    [AvaloniaFact]
    public async Task AnUpdateShowsItsDownloadThenOffersARestart()
    {
        var update = new HeldAppUpdate();
        using var ui = new UiHarness(settings => settings.Current.WelcomeOffered = true,
            services => services.AddSingleton<IAppUpdateService>(update));
        ui.Show();
        var chip = ui.Window.StatusBar.GetVisualDescendants().OfType<AppUpdateView>().Single();
        Assert.True(await UiHarness.WaitUntilAsync(() => ui.ViewModel.AppUpdate.IsAvailable));

        var installing = ui.ViewModel.AppUpdate.UpdateCommand.Execute().ToTask();
        Assert.True(await UiHarness.WaitUntilAsync(() => ui.ViewModel.AppUpdate.PercentText == "42%"));
        Assert.Contains("Updating to 0.2.0", chip.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
        ui.Screenshot("status_app_update_downloading.png");

        update.Finish.SetResult();
        await installing;
        UiHarness.Settle();
        Assert.Contains("Restart now", chip.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text));
        ui.Screenshot("status_app_update_ready.png");
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
        matchStats.Progress!.Report(new MatchFetchProgress(1, 120, 405, 203, 488,"09-29 · Emissary – Oracle · Enemies: Haze", 3_100_000));
        UiHarness.Settle();
        var running = ui.Window.StatusBar.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "Running");
        running.Flyout!.ShowAt(running);
        UiHarness.Settle();

        var card = ((Flyout)running.Flyout).Content as Control;
        var texts = card!.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains("09-29 · every match", texts);
        Assert.Contains("in use", texts);
        Assert.Contains("Enemies: Haze", texts);
        Assert.Contains("203 of 488 calls", texts);
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
        Assert.Contains("about 35 s · 1.3 MB", texts);
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

    /// <summary>How much of the model is filled in, and how reliable each kind of lift is, which only someone filling it in needs.</summary>
    [AvaloniaFact]
    public void CoverageShowsOnlyWithTheEditors()
    {
        using var ui = new UiHarness(settings => settings.Current.WelcomeOffered = true);
        ui.Show();
        Assert.False(ui.ViewModel.DataStatus.ShowsCoverage);
        Assert.Empty(ui.ViewModel.DataStatus.Coverage);
        Assert.Empty(ui.ViewModel.DataStatus.Families);

        ui.ViewModel.Settings.General.ShowModelEditors = true;

        Assert.True(ui.ViewModel.DataStatus.ShowsCoverage);
        Assert.NotEmpty(ui.ViewModel.DataStatus.Families);
        Assert.Equal(["Hero traits rated", "Items tagged", "Formula rules"], ui.ViewModel.DataStatus.Coverage.Select(fact => fact.Label));
        OpenCard(ui);
        ui.Screenshot("status_card_editors.png");
    }

    [AvaloniaFact]
    public void TheStatusBarZoomButtonsStepAndResetTheZoom()
    {
        using var ui = new UiHarness(settings => settings.Current.WelcomeOffered = true);
        ui.Show();
        var barHeight = ui.Window.StatusBar.Bounds.Height;
        var buttons = ZoomButtons(ui);
        Assert.Equal("100%", ui.ViewModel.ZoomText);
        Assert.Equal("100%", buttons[1].Content);
        Assert.False(buttons[1].IsEffectivelyEnabled);

        buttons[2].Command!.Execute(null);
        UiHarness.Settle();
        Assert.Equal("115%", buttons[1].Content);
        Assert.True(buttons[1].IsEffectivelyEnabled);

        buttons[0].Command!.Execute(null);
        buttons[0].Command!.Execute(null);
        UiHarness.Settle();
        Assert.Equal("85%", buttons[1].Content);

        buttons[1].Command!.Execute(null);
        UiHarness.Settle();
        Assert.Equal("100%", buttons[1].Content);
        Assert.Equal(barHeight, ui.Window.StatusBar.Bounds.Height);
        ui.Screenshot("status_zoom.png");
    }

    /// <summary>Within the pixel or two that rounding the zoomed layout moves them: a button is 18 tall.</summary>
    [AvaloniaFact]
    public void TheZoomButtonsKeepTheirPlaceWhileThePointerIsOnThem()
    {
        using var ui = new UiHarness();
        ui.Show();
        var buttons = ZoomButtons(ui);
        var before = buttons.Select(button => ScreenBounds(ui, button)).ToList();
        ui.Window.MouseMove(Center(ui, buttons[1]));

        for (var index = 0; index < ZoomLevels.Steps.Count; index++)
        {
            ui.Settings.Update(settings => settings.ZoomIndex = index);
            UiHarness.Settle();
            for (var button = 0; button < buttons.Count; button++)
            {
                var bounds = ScreenBounds(ui, buttons[button]);
                var shift = Math.Max(Math.Abs(bounds.X - before[button].X), Math.Abs(bounds.Y - before[button].Y));
                var growth = Math.Max(Math.Abs(bounds.Width - before[button].Width), Math.Abs(bounds.Height - before[button].Height));
                Assert.True(shift <= 2 && growth <= 0.5, $"Button {button} at {ZoomLevels.Steps[index]:P0} is {bounds}, not {before[button]}");
            }
        }
    }

    [AvaloniaFact]
    public async Task TheZoomButtonsEaseToTheirNewSizeOnceThePointerLeaves()
    {
        using var ui = new UiHarness();
        ui.Show();
        var stepper = ZoomStepper(ui);
        var at = Center(ui, ZoomButtons(ui)[2]);

        for (var step = 0; step < 3; step++)
            Click(ui, at);
        Assert.Equal(1.5, ui.ViewModel.UiScale);
        Assert.Equal(88, ScreenBounds(ui, stepper).Width, 0.01);

        var layoutPasses = 0;
        ui.Window.LayoutUpdated += (_, _) => layoutPasses++;
        ui.Window.MouseMove(new Point(100, 100));
        UiHarness.Settle();
        layoutPasses = 0;
        Assert.True(await UiHarness.WaitUntilAsync(() => Math.Abs(ScreenBounds(ui, stepper).Width - 88 * 1.5) < 0.01));
        Assert.Equal(18 * 1.5, ScreenBounds(ui, stepper).Height, 0.01);
        Assert.Equal(0, layoutPasses);

        ui.Settings.Update(settings => settings.ZoomIndex = 0);
        UiHarness.Settle();
        Assert.NotEqual(0, layoutPasses);
    }

    /// <summary>A tooltip under the pointer took the hover from its button, so the hold let go and the press missed.</summary>
    [AvaloniaFact]
    public async Task TheZoomButtonTooltipsOpenAboveAndLeaveTheHoverWithTheButton()
    {
        using var ui = new UiHarness();
        ui.Show();
        foreach (var button in ZoomButtons(ui).Where(button => button.IsEffectivelyEnabled))
        {
            var at = Center(ui, button);
            ui.Window.MouseMove(at);
            Assert.True(await UiHarness.WaitUntilAsync(() => ToolTip.GetIsOpen(button)));

            var tooltip = OverlayLayer.GetOverlayLayer(ui.Window)!.Children.OfType<OverlayPopupHost>().Single();
            Assert.True(tooltip.Bounds.Bottom <= ScreenBounds(ui, button).Top + 0.5, $"{tooltip.Bounds} is not above {button.Bounds}");
            Assert.True(button.IsPointerOver);
            Assert.True(ui.Window.InputHitTest(at) is Visual hit && (hit == button || button.IsVisualAncestorOf(hit)));

            ui.Window.MouseMove(new Point(100, 100));
            Assert.True(await UiHarness.WaitUntilAsync(() => !ToolTip.GetIsOpen(button)));
        }
    }

    [AvaloniaFact]
    public void TheZoomButtonsFollowAZoomAtOnceWhenThePointerIsElsewhere()
    {
        using var ui = new UiHarness();
        ui.Show();
        var stepper = ZoomStepper(ui);
        ui.Window.MouseMove(new Point(100, 100));

        ui.Settings.Update(settings => settings.ZoomIndex = 5);
        UiHarness.Settle();

        Assert.Equal(1.5, ui.ViewModel.UiScale);
        Assert.Equal(88 * 1.5, ScreenBounds(ui, stepper).Width, 0.01);
    }

    [AvaloniaFact]
    public async Task EnteringTheZoomButtonsAgainMidEaseHoldsTheSizeItReached()
    {
        var corner = new Border { Width = 100, Height = 20 };
        var window = new Window { Content = corner };
        window.Show();
        var hold = new ZoomCornerHold(corner, TimeSpan.FromSeconds(1));
        var drawing = (ScaleTransform)corner.RenderTransform!;

        hold.ZoomChanged(1);
        hold.Enter();
        hold.ZoomChanged(2);
        Assert.Equal(0.5, drawing.ScaleX);

        hold.Leave();
        Assert.True(await UiHarness.WaitUntilAsync(() => drawing.ScaleX > 0.7));
        hold.Enter();
        var reached = drawing.ScaleX;
        Assert.InRange(reached, 0.7, 1);

        await Task.Delay(300);
        UiHarness.Settle();
        Assert.Equal(reached, drawing.ScaleX);

        hold.Leave();
        Assert.True(await UiHarness.WaitUntilAsync(() => drawing.ScaleX == 1, TimeSpan.FromSeconds(5)));
        window.Close();
    }

    /// <summary>Clicking one spot again and again, as someone stepping the zoom does, never leaves the button, even where it's held past the bar's top.</summary>
    [AvaloniaFact]
    public void ClickingAZoomButtonRepeatedlyKeepsSteppingTheZoom()
    {
        using var ui = new UiHarness();
        ui.Show();
        var buttons = ZoomButtons(ui);
        var zoomOut = new Point(Center(ui, buttons[0]).X, ScreenBounds(ui, buttons[0]).Top + 1);
        var zoomIn = Center(ui, buttons[2]);

        for (var index = ZoomLevels.DefaultIndex + 1; index < ZoomLevels.Steps.Count; index++)
        {
            Click(ui, zoomIn);
            Assert.Equal(ZoomLevels.Steps[index], ui.ViewModel.UiScale);
        }
        for (var index = ZoomLevels.Steps.Count - 2; index >= 0; index--)
        {
            Click(ui, zoomOut);
            Assert.Equal(ZoomLevels.Steps[index], ui.ViewModel.UiScale);
        }
    }

    [AvaloniaFact]
    public void ScrollingOverTheZoomButtonsZoomsWithoutCtrl()
    {
        using var ui = new UiHarness();
        ui.Show();
        var level = Center(ui, ZoomButtons(ui)[1]);

        ui.Window.MouseWheel(level, new Vector(0, 1));
        Assert.Equal(1.15, ui.ViewModel.UiScale);
        ui.Window.MouseWheel(level, new Vector(0, -1));
        ui.Window.MouseWheel(level, new Vector(0, -1));
        Assert.Equal(0.85, ui.ViewModel.UiScale);

        var elsewhere = ui.Window.StatusBar.TranslatePoint(new Point(ui.Window.StatusBar.Bounds.Width / 2, 5), ui.Window)!.Value;
        ui.Window.MouseWheel(elsewhere, new Vector(0, 1));
        Assert.Equal(0.85, ui.ViewModel.UiScale);
    }

    [AvaloniaFact]
    public void AnOfflineChipShowsOnlyWhileTheAppCannotReachTheInternet()
    {
        using var ui = new UiHarness();
        ui.Show();
        var view = ui.Window.StatusBar.GetVisualDescendants().OfType<ConnectionView>().Single();
        var chip = OfflineChip(ui);
        var barHeight = ui.Window.StatusBar.Bounds.Height;
        Assert.False(chip.IsVisible);
        Assert.Equal(0, view.Bounds.Width);

        ui.Connectivity.GoOffline();
        UiHarness.Settle();

        Assert.True(chip.IsEffectivelyVisible);
        Assert.Equal(["Offline"], chip.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
        var tip = (string)ToolTip.GetTip(chip)!;
        Assert.StartsWith("No internet connection.", tip);
        Assert.Contains("Click to check now.", tip);
        Assert.Contains("Hero art hasn't been downloaded yet", tip);
        Assert.Equal(barHeight, ui.Window.StatusBar.Bounds.Height);
        ui.Screenshot("status_offline.png");

        ui.Connectivity.Reconnect();
        UiHarness.Settle();

        Assert.False(chip.IsVisible);
        Assert.Equal(0, view.Bounds.Width);
        Assert.Equal(barHeight, ui.Window.StatusBar.Bounds.Height);
    }

    [AvaloniaFact]
    public void ClickingTheOfflineChipChecksAgainAndItReadsCheckingUntilTheAnswer()
    {
        using var ui = new UiHarness();
        ui.Show();
        ui.Connectivity.GoOffline();
        UiHarness.Settle();
        var chip = OfflineChip(ui);

        Click(ui, Center(ui, chip));
        Assert.Equal(1, ui.Connectivity.Retries);

        ui.Connectivity.StartChecking();
        UiHarness.Settle();
        Assert.Equal(["Checking…"], chip.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
        Assert.False(chip.IsEffectivelyEnabled);
        ui.Screenshot("status_offline_checking.png");

        ui.Connectivity.GoOffline();
        UiHarness.Settle();
        Assert.Equal(["Offline"], chip.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
        Assert.True(chip.IsEffectivelyEnabled);
    }

    private static Button OfflineChip(UiHarness ui) =>
        ui.Window.StatusBar.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "Retry");

    private static StackPanel ZoomStepper(UiHarness ui) =>
        ui.Window.StatusBar.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "ZoomStepper");

    private static List<Button> ZoomButtons(UiHarness ui) => ZoomStepper(ui).Children.OfType<Button>().ToList();

    private static Rect ScreenBounds(UiHarness ui, Control control) =>
        new(control.TranslatePoint(default, ui.Window)!.Value,
            control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), ui.Window)!.Value);

    private static Point Center(UiHarness ui, Control control) => ScreenBounds(ui, control).Center;

    private static void Click(UiHarness ui, Point at)
    {
        ui.Window.MouseMove(at);
        ui.Window.MouseDown(at, MouseButton.Left);
        ui.Window.MouseUp(at, MouseButton.Left);
        UiHarness.Settle();
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
