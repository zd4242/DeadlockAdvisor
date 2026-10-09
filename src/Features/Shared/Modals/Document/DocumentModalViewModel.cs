using System.Windows.Input;
using DeadlockAdvisor.Core;

namespace DeadlockAdvisor.Features.Shared.Modals.Document;

/// <summary>
/// A long read-only text, such as a license file: a title, a scrolling body and a Close button. A single text
/// control laying out hundreds of kilobytes freezes the window on every selection change, so the body is handed
/// to the view as blocks of pre-wrapped lines for a virtualized list. The blocks are all the same height (fixed
/// line height, no wrapping in the view), because with uneven heights the scroll bar's estimate of the total
/// keeps changing as blocks are realized and the thumb jumps about.
/// </summary>
public class DocumentModalViewModel(string title, string text, ICommand closeCommand) : ViewModelBase
{
    public const int Columns = 90;
    public const int LinesPerBlock = 24;

    public string Title { get; } = title;
    public string Text { get; } = text;
    public IReadOnlyList<string> Blocks { get; } = Wrap(text).Chunk(LinesPerBlock).Select(lines => string.Join('\n', lines)).ToArray();
    public ICommand CloseCommand { get; } = closeCommand;

    /// <summary>Breaks each line that's too wide at a space, indenting what's left like the line it came from.</summary>
    internal static IEnumerable<string> Wrap(string text)
    {
        foreach (var line in text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'))
        {
            var rest = line.TrimEnd().Replace("\t", "    ");
            var indent = new string(' ', Math.Min(rest.Length - rest.TrimStart().Length, 20));
            while (rest.Length > Columns)
            {
                var cut = rest.LastIndexOf(' ', Columns, Columns - indent.Length);
                if (cut <= indent.Length)
                    cut = Columns;
                yield return rest[..cut].TrimEnd();
                rest = indent + rest[cut..].TrimStart();
            }
            yield return rest;
        }
    }
}
