using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace DeadlockAdvisor.Controls;

/// <summary>A run of text in <see cref="RichText"/>. A null colour keeps the block's own; a lone "\n" breaks the line.</summary>
public sealed record TextSpan(string Text, Color? Color = null, bool Bold = false, bool Italic = false)
{
    public static readonly TextSpan LineBreak = new("\n");
}

/// <summary>
/// A text block built from styled spans, for the few labels that mix styles: a bold
/// name, a faint aside, a word in its relation's colour.
/// </summary>
public class RichText : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<TextSpan>?> SpansProperty =
        AvaloniaProperty.Register<RichText, IReadOnlyList<TextSpan>?>(nameof(Spans));

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public IReadOnlyList<TextSpan>? Spans
    {
        get => GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    /// <summary>A wrapped block of spans, e.g. for a tooltip.</summary>
    public static RichText Create(IReadOnlyList<TextSpan> spans) => new() { Spans = spans, TextWrapping = TextWrapping.Wrap };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SpansProperty)
            Inlines = ToInlines(Spans ?? []);
    }

    public static InlineCollection ToInlines(IEnumerable<TextSpan> spans)
    {
        var inlines = new InlineCollection();
        foreach (var span in spans)
        {
            if (ReferenceEquals(span, TextSpan.LineBreak) || span.Text == "\n")
            {
                inlines.Add(new LineBreak());
                continue;
            }
            var run = new Run(span.Text);
            if (span.Color is { } color)
                run.Foreground = new SolidColorBrush(color);
            if (span.Bold)
                run.FontWeight = FontWeight.Bold;
            if (span.Italic)
                run.FontStyle = FontStyle.Italic;
            inlines.Add(run);
        }
        return inlines;
    }
}
