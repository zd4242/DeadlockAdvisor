using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Shared.Modals.Base;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Services.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace DeadlockAdvisor.Tests.Ui;

public class ShortcutTests
{
    /// <summary>As Qt's window-wide shortcuts: they work from anywhere on the Match page, not only with focus inside it.</summary>
    [AvaloniaFact]
    public async Task MatchShortcutsWorkWithoutFocusOnlyOnTheMatchPage()
    {
        using var ui = new UiHarness();
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
        using var ui = new UiHarness();
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
}
