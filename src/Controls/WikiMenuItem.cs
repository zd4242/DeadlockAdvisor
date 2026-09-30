using Avalonia.Interactivity;
using DeadlockAdvisor.Core;

namespace DeadlockAdvisor.Controls;

/// <summary>A menu entry that opens a hero's or item's page on the Deadlock wiki in the browser.</summary>
public class WikiMenuItem : MenuItem
{
    public const string Text = "Open on Deadlock Wiki";

    public static readonly StyledProperty<string?> PageProperty =
        AvaloniaProperty.Register<WikiMenuItem, string?>(nameof(Page));

    public WikiMenuItem()
    {
        Header = Text;
    }

    public WikiMenuItem(string page) : this()
    {
        Page = page;
    }

    protected override Type StyleKeyOverride => typeof(MenuItem);

    /// <summary>The page's title: the hero's or item's in-game name.</summary>
    public string? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public Uri? Url => string.IsNullOrWhiteSpace(Page) ? null : Wiki.PageUrl(Page);

    /// <summary>A menu of just this entry at the pointer, for drawn controls with no element per row to hang a menu on.</summary>
    public static void ShowMenu(Control target, string page) =>
        PointerMenu.Show(target, [new WikiMenuItem(page)]);

    protected override void OnClick(RoutedEventArgs e)
    {
        base.OnClick(e);
        if (Url is { } url)
            _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(url);
    }
}
