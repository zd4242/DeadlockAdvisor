using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.MainWindow;
using DeadlockAdvisor.Features.Shared.Modals.Base;
using DeadlockAdvisor.Features.Shared.Modals.Confirmation;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DeadlockAdvisor.Tests.Ui;

public class ShortcutTests
{
    /// <summary>As Qt's window-wide shortcuts: they work from anywhere on the Match page, not only with focus inside it.</summary>
    [AvaloniaFact]
    public async Task MatchShortcutsWorkWithoutFocusOnlyOnTheMatchPage()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings));
        // With art to match against, F9 goes as far as asking for a capture (which fails: no screen in tests).
        Support.VisionData.CopyTopbarInto(ui.Data.AssetsDir);
        ui.Show();
        ui.Window.FocusManager!.ClearFocus();
        var board = ui.ViewModel.Match.Board;
        bool MessageShown() =>
            ui.Window.OwnedWindows.OfType<ModalWindow>().SingleOrDefault()?.DataContext is ModalViewModel { Content: MessageModalViewModel };

        ui.Window.KeyPressQwerty(PhysicalKey.Digit3, RawInputModifiers.Alt);
        UiHarness.Settle();
        Assert.Equal(Role.Ally, board.Mode);

        ui.Window.KeyPressQwerty(PhysicalKey.F9, RawInputModifiers.None);
        Assert.True(await UiHarness.WaitUntilAsync(MessageShown));
        Assert.Equal(1, ui.Capture.Captures);
        ui.Services.GetRequiredService<IModalService>().CloseModal();

        ui.ViewModel.CurrentPage = 1;
        UiHarness.Settle();
        ui.Window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.Alt);
        ui.Window.KeyPressQwerty(PhysicalKey.F9, RawInputModifiers.None);

        Assert.False(await UiHarness.WaitUntilAsync(MessageShown, TimeSpan.FromMilliseconds(500)));
        Assert.Equal(Role.Ally, board.Mode);
        Assert.Equal(1, ui.Capture.Captures);
    }

    [AvaloniaFact]
    public void FunctionKeysRandomizeTheMatchKeepingWhatTheyName()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings));
        ui.Show();
        ui.Window.FocusManager!.ClearFocus();
        var board = ui.ViewModel.Match.Board;
        string? Self() => board.AllySlots.SingleOrDefault(slot => slot.IsSelf)?.HeroId;
        HashSet<string?> Team() => board.AllySlots.Select(slot => slot.HeroId).ToHashSet();

        ui.Window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.All(board.AllySlots.Concat(board.EnemySlots), slot => Assert.NotNull(slot.HeroId));

        var self = Self();
        ui.Window.KeyPressQwerty(PhysicalKey.F7, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.Equal(self, Self());

        var team = Team();
        ui.Window.KeyPressQwerty(PhysicalKey.F8, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.Equal(team, Team());

        ui.ViewModel.CurrentPage = 1;
        UiHarness.Settle();
        ui.Window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.Equal(team, Team());
    }

    [AvaloniaFact]
    public void HidingTheRandomButtonsTurnsTheirKeysOffToo()
    {
        using var ui = new UiHarness();
        ui.Show();
        ui.Window.FocusManager!.ClearFocus();
        var board = ui.ViewModel.Match.Board;
        var more = ui.Window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "MoreButton");
        var menu = (MenuFlyout)more.Flyout!;
        List<MenuItem> Randoms() => menu.Items.OfType<MenuItem>().Where(item => ((string)item.Header!).StartsWith("Random", StringComparison.Ordinal)).ToList();
        menu.ShowAt(more);
        UiHarness.Settle();
        Assert.Equal(3, Randoms().Count);
        Assert.All(Randoms(), item => Assert.True(item.IsVisible));
        menu.Hide();

        ui.ViewModel.Settings.General.ShowRandomButtons = false;
        UiHarness.Settle();
        ui.Window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None);
        UiHarness.Settle();

        Assert.False(ui.Settings.Current.ShowRandomButtons);
        menu.ShowAt(more);
        UiHarness.Settle();
        Assert.All(Randoms(), item => Assert.False(item.IsVisible));
        menu.Hide();
        Assert.All(board.AllySlots.Concat(board.EnemySlots), slot => Assert.Null(slot.HeroId));
    }

    [AvaloniaFact]
    public void AMovedKeyRunsItsActionAndTheOldOneNoLongerDoes()
    {
        using var ui = new UiHarness();
        ui.Show();
        ui.Window.FocusManager!.ClearFocus();
        var board = ui.ViewModel.Match.Board;
        var detectKey = ui.Window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("shortcut"));
        Assert.Equal("F9", detectKey.Text);
        Assert.True(detectKey.IsEffectivelyVisible);

        ui.Settings.Update(s =>
        {
            s.SetGesture(ShortcutAction.Randomize, new KeyGesture(Key.F2));
            s.SetGesture(ShortcutAction.Detect, null);
        });
        UiHarness.Settle();
        Assert.False(detectKey.IsEffectivelyVisible);
        Assert.Equal(new KeyGesture(Key.F2), board.RandomGesture);

        ui.Window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.All(board.AllySlots.Concat(board.EnemySlots), slot => Assert.Null(slot.HeroId));

        ui.Window.KeyPressQwerty(PhysicalKey.F2, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.All(board.AllySlots.Concat(board.EnemySlots), slot => Assert.NotNull(slot.HeroId));
    }

    /// <summary>The fixed keys stay in XAML, so a rebindable one mustn't be allowed onto any of them.</summary>
    [AvaloniaFact]
    public void NoShortcutCanBeMovedOntoOneOfTheWindowsFixedKeys()
    {
        using var ui = new UiHarness();
        var rebindable = ui.ViewModel.ShortcutBindings.Select(binding => binding.Command).ToHashSet();

        var fixedKeys = ui.Window.KeyBindings.Where(binding => !rebindable.Contains(binding.Command)).ToList();

        Assert.NotEmpty(fixedKeys);
        Assert.All(fixedKeys, binding => Assert.NotNull(ShortcutKeys.Problem(binding.Gesture)));
    }

    /// <summary>F9 held system-wide: from the game, it detects onto the Match page and, when told to, brings the window up for what that shows.</summary>
    [AvaloniaFact]
    public async Task F9FromAnotherAppDetectsOntoTheMatchPageAndBringsTheWindowUp()
    {
        using var ui = new UiHarness(settings =>
        {
            UiHarness.Editing(settings, 1);
            settings.Current.ComeUpForReview = true;
        });
        Support.VisionData.CopyTopbarInto(ui.Data.AssetsDir);
        ui.Show();
        var broughtForward = 0;
        using var _ = ui.ViewModel.ViewInteraction
            .Subscribe(action => broughtForward += action == MainWindowViewModel.BringForwardAction ? 1 : 0);
        var detecting = false;
        using var __ = ui.ViewModel.DetectFromAnywhereCommand.IsExecuting.Subscribe(executing => detecting = executing);
        bool MessageShown() =>
            ui.Window.OwnedWindows.OfType<ModalWindow>().SingleOrDefault()?.DataContext is ModalViewModel { Content: MessageModalViewModel };
        ui.Foreground.IsAnotherAppInFront = true;

        ui.Hotkey.Press();

        // No screen in tests, so the capture fails and says so: that's the thing to come up for.
        Assert.True(await UiHarness.WaitUntilAsync(MessageShown));
        Assert.True(ui.ViewModel.IsMatchPage);
        Assert.Equal(1, ui.Capture.Captures);
        Assert.Equal(1, broughtForward);

        // Pressed again with that still up, it brings it back rather than detecting behind it. The first
        // press's detect winds down just after the message shows, and a press before then is dropped.
        Assert.True(await UiHarness.WaitUntilAsync(() => !detecting));
        ui.Hotkey.Press();
        Assert.True(await UiHarness.WaitUntilAsync(() => broughtForward == 2));
        Assert.Equal(1, ui.Capture.Captures);
    }

    /// <summary>By default, F9 in the game leaves what it found waiting behind it until pressed again.</summary>
    [AvaloniaFact]
    public async Task F9FromTheGameLeavesWhatItFoundWaitingUntilPressedAgain()
    {
        using var ui = new UiHarness();
        Support.VisionData.CopyTopbarInto(ui.Data.AssetsDir);
        ui.Show();
        var broughtForward = 0;
        using var _ = ui.ViewModel.ViewInteraction
            .Subscribe(action => broughtForward += action == MainWindowViewModel.BringForwardAction ? 1 : 0);
        var modals = ui.Services.GetRequiredService<IModalService>();
        var detecting = false;
        using var __ = ui.ViewModel.DetectFromAnywhereCommand.IsExecuting.Subscribe(executing => detecting = executing);
        bool MessageShown() =>
            ui.Window.OwnedWindows.OfType<ModalWindow>().SingleOrDefault()?.DataContext is ModalViewModel { Content: MessageModalViewModel };
        ui.Foreground.IsAnotherAppInFront = true;

        ui.Hotkey.Press();

        // A press while the last is still finishing would be dropped.
        Assert.True(await UiHarness.WaitUntilAsync(() => ui.Capture.Captures == 1 && modals.IsModalOpen && !detecting));
        Assert.False(MessageShown());
        Assert.Equal(0, broughtForward);

        ui.Hotkey.Press();
        Assert.True(await UiHarness.WaitUntilAsync(MessageShown));
        Assert.Equal(1, ui.Capture.Captures);
        Assert.Equal(1, broughtForward);
    }

    /// <summary>A modal closed while it waited for the window, such as Detect's progress, never opens.</summary>
    [AvaloniaFact]
    public async Task AModalClosedWhileWaitingBehindAnotherAppNeverOpens()
    {
        using var ui = new UiHarness();
        ui.Show();
        var modals = ui.Services.GetRequiredService<IModalService>();
        ui.Foreground.IsAnotherAppInFront = true;

        modals.ShowMessage("Waiting", "Behind the game");
        modals.CloseModal();
        UiHarness.Settle();
        Assert.Empty(ui.Window.OwnedWindows.OfType<ModalWindow>());

        // With no art, F9 offers to download it, and comes up for that alone. The art is looked for off
        // the UI thread, so the offer takes a moment.
        ui.Foreground.IsAnotherAppInFront = false;
        ui.Hotkey.Press();

        Assert.True(await UiHarness.WaitUntilAsync(() => ui.Window.OwnedWindows.OfType<ModalWindow>().Any()));
        var modal = Assert.Single(ui.Window.OwnedWindows.OfType<ModalWindow>());
        Assert.IsType<ConfirmationModalViewModel>(((ModalViewModel)modal.DataContext!).Content);
    }

    /// <summary>Tests must never take F9 from the desktop; a window without a native handle doesn't try.</summary>
    [AvaloniaFact]
    public void TheHotkeyServiceStandsDownWithoutANativeWindow()
    {
        var settings = new FakeSettingsService();
        using var hotkey = new GlobalHotkeyService(settings, new FakeLoggingService());
        var window = new Window();
        var statuses = new List<HotkeyStatus>();
        using var _ = hotkey.Status.Subscribe(statuses.Add);

        hotkey.Attach(window);
        settings.Update(s => s.DetectFromAnywhere = true);

        Assert.Equal([HotkeyStatus.Unsupported], statuses);
        window.Close();
    }
}
