using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Controls.Art;
using DeadlockAdvisor.Features.Match.Import;
using DeadlockAdvisor.Features.Shared.Modals.Base;

namespace DeadlockAdvisor.Tests.Ui;

public class ImportMatchTests
{
    [AvaloniaFact]
    public async Task TheMatchBarImportsAMatchWithTheKeyboard()
    {
        using var ui = new UiHarness(settings => settings.Current.SteamAccountId = 1003);
        MatchImportTests.Serve(ui.Api);
        ui.Show();

        var more = ui.Window.MatchPage.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "MoreButton");
        var menu = (MenuFlyout)more.Flyout!;
        menu.ShowAt(more);
        UiHarness.Settle();
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Import match by ID…")).Command!.Execute(null);
        menu.Hide();
        UiHarness.Settle();

        var modal = ui.Window.OwnedWindows.OfType<ModalWindow>().Single();
        var dialog = Assert.IsType<ImportMatchViewModel>(((ModalViewModel)modal.DataContext!).Content);
        var box = modal.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "MatchId");
        Assert.True(await UiHarness.WaitUntilAsync(() => box.IsFocused));
        ui.ScreenshotModal("import_match_empty.png");

        // Enter looks the match up; your saved account picks you out, so a second Enter applies it.
        modal.KeyTextInput("108474234");
        modal.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.True(await UiHarness.WaitUntilAsync(() => dialog.HasMatch && dialog.Self is not null));
        Assert.True(await UiHarness.WaitUntilAsync(() => dialog.Self!.PlayerName.Length > 0));
        var portraits = modal.GetVisualDescendants().OfType<ArtImage>().ToList();
        Assert.Equal(12, portraits.Count);
        Assert.All(portraits, portrait => Assert.NotNull(ArtHost.GetService(portrait)));
        ui.ScreenshotModal("import_match.png");

        modal.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.True(await UiHarness.WaitUntilAsync(() => ui.ViewModel.Match.Match.SelfHero == "haze"));
        Assert.Empty(ui.Window.OwnedWindows.OfType<ModalWindow>());
    }
}
