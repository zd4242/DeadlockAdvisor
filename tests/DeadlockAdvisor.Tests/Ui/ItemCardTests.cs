using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using DeadlockAdvisor.Controls.Art;
using DeadlockAdvisor.Features.Shared.ItemCard;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Tests.Ui;

public class ItemCardTests
{
    [AvaloniaFact]
    public void CardsRenderForEveryKindOfItem()
    {
        using var ui = new UiHarness();
        var store = ui.Data.Store;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 24,
            Margin = new Thickness(16),
            VerticalAlignment = VerticalAlignment.Top,
        };
        foreach (var itemId in new[] { "slowing_bullets", "weighted_shots", "warp_stone", "silence_wave", "extra_health" })
        {
            var card = ItemCardBuilder.Build(store, store.Items[itemId]);
            card.Width = ItemCardBuilder.Width;
            card.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(card);
        }

        var window = new Window
        {
            Width = 1850,
            Height = 560,
            Background = new SolidColorBrush(Palette.Bg),
            Content = row,
        };
        ArtHost.SetService(window, ui.Art);
        window.Show();
        UiHarness.Settle();

        var frame = window.CaptureRenderedFrame()!;
        frame.Save(UiHarness.MockupPath("item_cards.png"));
        window.Close();

        Assert.All(row.Children, card => Assert.True(card.Bounds.Height > 80));
    }
}
