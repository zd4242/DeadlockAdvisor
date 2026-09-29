using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.MainWindow;
using DeadlockAdvisor.Features.Shared.Modals.Base;
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

        ui.Window.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.Alt);
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
        var random = ui.Window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Random"));
        Assert.True(random.IsEffectivelyVisible);

        ui.ViewModel.Settings.General.ShowRandomButtons = false;
        UiHarness.Settle();
        ui.Window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None);
        UiHarness.Settle();

        Assert.False(ui.Settings.Current.ShowRandomButtons);
        Assert.False(random.IsEffectivelyVisible);
        Assert.All(board.AllySlots.Concat(board.EnemySlots), slot => Assert.Null(slot.HeroId));
    }

    /// <summary>F9 held system-wide: from the game, it detects onto the Match page and brings the window up for what that shows.</summary>
    [AvaloniaFact]
    public async Task F9FromAnotherAppDetectsOntoTheMatchPageAndBringsTheWindowUp()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings, 1));
        Support.VisionData.CopyTopbarInto(ui.Data.AssetsDir);
        ui.Show();
        var broughtForward = 0;
        using var _ = ui.ViewModel.ViewInteraction
            .Subscribe(action => broughtForward += action == MainWindowViewModel.BringForwardAction ? 1 : 0);
        bool MessageShown() =>
            ui.Window.OwnedWindows.OfType<ModalWindow>().SingleOrDefault()?.DataContext is ModalViewModel { Content: MessageModalViewModel };

        ui.Hotkey.Press();

        // No screen in tests, so the capture fails and says so: that's the thing to come up for.
        Assert.True(await UiHarness.WaitUntilAsync(MessageShown));
        Assert.True(ui.ViewModel.IsMatchPage);
        Assert.Equal(1, ui.Capture.Captures);
        Assert.Equal(1, broughtForward);

        // Pressed again with that still up, it brings it back rather than detecting behind it.
        ui.Hotkey.Press();
        UiHarness.Settle();
        Assert.Equal(1, ui.Capture.Captures);
        Assert.Equal(2, broughtForward);
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
