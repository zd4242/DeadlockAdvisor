using Avalonia.Interactivity;
using DeadlockAdvisor.Core;

namespace DeadlockAdvisor.Controls;

/// <summary>What a wiki page is about, which the menu entry names.</summary>
public enum WikiKind
{
    Item,
    Hero,
}

/// <summary>A menu entry that opens a hero's or item's page on the Deadlock wiki in the browser.</summary>
public class WikiMenuItem : MenuItem
{
    public const string ItemText = "Open item on Deadlock Wiki";
    public const string HeroText = "Open hero on Deadlock Wiki";

    public static readonly StyledProperty<string?> PageProperty =
        AvaloniaProperty.Register<WikiMenuItem, string?>(nameof(Page));

    public static readonly StyledProperty<WikiKind> KindProperty =
        AvaloniaProperty.Register<WikiMenuItem, WikiKind>(nameof(Kind));

    public WikiMenuItem()
    {
        Header = TextFor(Kind);
    }

    public WikiMenuItem(string page, WikiKind kind) : this()
    {
        Page = page;
        Kind = kind;
    }

    public static string TextFor(WikiKind kind) => kind == WikiKind.Hero ? HeroText : ItemText;

    protected override Type StyleKeyOverride => typeof(MenuItem);

    /// <summary>The page's title: the hero's or item's in-game name.</summary>
    public string? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    /// <summary>Whether the page is an item's or a hero's; items unless said otherwise.</summary>
    public WikiKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Uri? Url => string.IsNullOrWhiteSpace(Page) ? null : Wiki.PageUrl(Page);

    /// <summary>A menu of just this entry at the pointer, for drawn controls with no element per row to hang a menu on.</summary>
    public static void ShowMenu(Control target, string page, WikiKind kind) =>
        PointerMenu.Show(target, [new WikiMenuItem(page, kind)]);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty)
            Header = TextFor(Kind);
    }

    protected override void OnClick(RoutedEventArgs e)
    {
        base.OnClick(e);
        if (Url is { } url)
            _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(url);
    }
}
