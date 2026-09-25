using System.Net;
using System.Text.RegularExpressions;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Features.Shared.ItemCard;

/// <summary>
/// Turns a stored tooltip description (the API's HTML cut down to &lt;b&gt;, &lt;i&gt;, &lt;br&gt; and
/// coloured spans) into text runs in the card's colours: highlighted words bright, footnotes faint.
/// </summary>
public static partial class TooltipMarkup
{
    [GeneratedRegex(@"<(/?)(\w+)([^>]*)>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"color:\s*(#[0-9a-fA-F]{3,8})")]
    private static partial Regex Color();

    public static InlineCollection ToInlines(string markup, IBrush baseBrush)
    {
        var inlines = new InlineCollection();
        // Each open tag pushes a style; closing it pops back to whatever was underneath.
        var styles = new Stack<(IBrush Brush, FontWeight Weight, FontStyle Style)>();
        styles.Push((baseBrush, FontWeight.Normal, FontStyle.Normal));

        var position = 0;
        foreach (System.Text.RegularExpressions.Match tag in Tag().Matches(markup))
        {
            AddText(inlines, markup[position..tag.Index], styles.Peek());
            position = tag.Index + tag.Length;

            var closing = tag.Groups[1].Value == "/";
            var name = tag.Groups[2].Value.ToLowerInvariant();
            if (name == "br")
            {
                inlines.Add(new LineBreak());
                continue;
            }
            if (closing)
            {
                if (styles.Count > 1)
                    styles.Pop();
                continue;
            }

            var current = styles.Peek();
            styles.Push(name switch
            {
                "b" => (new SolidColorBrush(Palette.Text), FontWeight.Bold, current.Style),
                "i" => (new SolidColorBrush(Palette.TextFaint), current.Weight, FontStyle.Italic),
                "span" when Color().Match(tag.Groups[3].Value) is { Success: true } color =>
                    (new SolidColorBrush(Avalonia.Media.Color.Parse(color.Groups[1].Value)), current.Weight, current.Style),
                _ => current,
            });
        }
        AddText(inlines, markup[position..], styles.Peek());
        return inlines;
    }

    private static void AddText(InlineCollection inlines, string text, (IBrush Brush, FontWeight Weight, FontStyle Style) style)
    {
        if (text.Length == 0)
            return;
        inlines.Add(new Run(WebUtility.HtmlDecode(text))
        {
            Foreground = style.Brush,
            FontWeight = style.Weight,
            FontStyle = style.Style,
        });
    }
}
