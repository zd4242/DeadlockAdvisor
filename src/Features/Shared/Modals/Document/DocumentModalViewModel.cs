using System.Text.RegularExpressions;
using System.Windows.Input;
using DeadlockAdvisor.Core;

namespace DeadlockAdvisor.Features.Shared.Modals.Document;

/// <summary>
/// A long read-only text, such as a license file: a title, a scrolling body and a Close button. The body is
/// handed to the view as paragraphs, because one text control laying out hundreds of kilobytes freezes the
/// window on every selection change, while a virtualized list only lays out what's on screen.
/// </summary>
public partial class DocumentModalViewModel(string title, string text, ICommand closeCommand) : ViewModelBase
{
    public string Title { get; } = title;
    public string Text { get; } = text;
    public IReadOnlyList<string> Paragraphs { get; } = Split(text);
    public ICommand CloseCommand { get; } = closeCommand;

    private static string[] Split(string text) =>
        BlankLines().Split(text.ReplaceLineEndings("\n").Trim('\n')).Where(paragraph => paragraph.Length > 0).ToArray();

    [GeneratedRegex(@"\n[ \t]*\n+")]
    private static partial Regex BlankLines();
}
