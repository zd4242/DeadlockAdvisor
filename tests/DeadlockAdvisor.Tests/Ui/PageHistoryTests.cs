using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace DeadlockAdvisor.Tests.Ui;

public class PageHistoryTests
{
    /// <summary>The mouse's back and forward buttons walk the pages opened, and a new page drops the forward ones.</summary>
    [AvaloniaFact]
    public void TheSideButtonsStepBackAndForwardThroughThePagesOpened()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings));
        ui.Show();
        var at = new Point(ui.Window.Bounds.Width / 2, ui.Window.Bounds.Height / 2);
        void Back() => ClickSideButton(ui.Window, at, MouseButton.XButton1);
        void Forward() => ClickSideButton(ui.Window, at, MouseButton.XButton2);

        ui.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
        ui.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
        Assert.Equal(2, ui.ViewModel.CurrentPage);

        Back();
        Assert.Equal(1, ui.ViewModel.CurrentPage);
        Back();
        Assert.Equal(0, ui.ViewModel.CurrentPage);
        Back();
        Assert.Equal(0, ui.ViewModel.CurrentPage);

        Forward();
        Assert.Equal(1, ui.ViewModel.CurrentPage);

        // Opening a page from here starts a new branch, so the page that was forward is gone.
        ui.ViewModel.ShowPageCommand.Execute(3).Subscribe();
        Forward();
        Assert.Equal(3, ui.ViewModel.CurrentPage);
        Back();
        Assert.Equal(1, ui.ViewModel.CurrentPage);
    }

    /// <summary>With Settings open, the back button closes it as before, and only the next press goes back.</summary>
    [AvaloniaFact]
    public void TheBackButtonClosesSettingsBeforeGoingBack()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings));
        ui.Show();
        var at = new Point(ui.Window.Bounds.Width / 2, ui.Window.Bounds.Height / 2);
        ui.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
        ui.ViewModel.OpenSettingsCommand.Execute().Subscribe();
        UiHarness.Settle();

        ClickSideButton(ui.Window, at, MouseButton.XButton1);
        Assert.False(ui.ViewModel.IsSettingsOpen);
        Assert.Equal(1, ui.ViewModel.CurrentPage);

        ClickSideButton(ui.Window, at, MouseButton.XButton1);
        Assert.Equal(0, ui.ViewModel.CurrentPage);
    }

    /// <summary>A model editor's page hidden since is skipped over, as if it had never been opened.</summary>
    [AvaloniaFact]
    public void BackSkipsTheEditorPagesHiddenSinceTheyWereOpened()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings));
        ui.Show();
        var at = new Point(ui.Window.Bounds.Width / 2, ui.Window.Bounds.Height / 2);
        ui.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
        ui.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
        Assert.Equal(2, ui.ViewModel.CurrentPage);

        ui.Settings.Update(s => s.ShowModelEditors = false);
        UiHarness.Settle();
        Assert.Equal(0, ui.ViewModel.CurrentPage);

        ClickSideButton(ui.Window, at, MouseButton.XButton1);
        Assert.Equal(1, ui.ViewModel.CurrentPage);
    }

    private static void ClickSideButton(Window window, Point at, MouseButton button)
    {
        window.MouseDown(at, button);
        window.MouseUp(at, button);
        UiHarness.Settle();
    }
}
