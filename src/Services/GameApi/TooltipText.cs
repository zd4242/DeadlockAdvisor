using System.Text;
using System.Text.RegularExpressions;

namespace DeadlockAdvisor.Services.GameApi;

/// <summary>
/// The API's description HTML cut down to what the item card renders: highlighted words as &lt;b&gt;,
/// footnotes as &lt;i&gt;, coloured stat names as coloured spans, line breaks. Inline icons (SVGs,
/// images) are dropped; the stat name they illustrate sits right next to them anyway.
/// </summary>
public sealed partial class TooltipText : PyHtmlParser
{
    private readonly StringBuilder _out = new();
    // One per open <span>: what closes it, or "" if it printed nothing.
    private readonly Stack<string> _closers = new();
    private int _inSvg;

    private TooltipText()
    {
    }

    public static string From(string? source)
    {
        var parser = new TooltipText();
        parser.Feed(source ?? "");
        parser.Close();
        var text = SpaceRun().Replace(parser._out.ToString(), " ");
        return PyText.Strip(SpacedBreak().Replace(text, "<br>"));
    }

    protected override void HandleStartTag(string tag, IReadOnlyList<(string Name, string? Value)> attrs)
    {
        if (tag == "svg")
            _inSvg++;
        if (_inSvg > 0)
            return;

        if (tag == "br")
        {
            _out.Append("<br>");
        }
        else if (tag == "span")
        {
            // As dict(attrs): a repeated attribute's last value wins.
            string? classAttr = null;
            string? style = null;
            foreach (var (name, value) in attrs)
            {
                if (name == "class")
                    classAttr = value;
                else if (name == "style")
                    style = value;
            }
            var classes = PyText.Split(classAttr ?? "");
            var color = Color().Match(style ?? "");
            var (opener, closer) = classes.Contains("highlight") ? ("<b>", "</b>")
                : classes.Contains("diminish") ? ("<i>", "</i>")
                : color.Success ? ($"<span style=\"color:{color.Groups[1].Value}\">", "</span>")
                : ("", "");
            _out.Append(opener);
            _closers.Push(closer);
        }
    }

    protected override void HandleEndTag(string tag)
    {
        if (tag == "svg")
            _inSvg = Math.Max(0, _inSvg - 1);
        else if (tag == "span" && _inSvg == 0 && _closers.Count > 0)
            _out.Append(_closers.Pop());
    }

    protected override void HandleData(string data)
    {
        if (_inSvg == 0)
            _out.Append(PyText.Escape(data));
    }

    [GeneratedRegex(@"color:" + PyText.Space + "*(#[0-9a-fA-F]{3,8})")]
    private static partial Regex Color();

    [GeneratedRegex(PyText.Space + "+")]
    private static partial Regex SpaceRun();

    [GeneratedRegex(PyText.Space + "*<br>" + PyText.Space + "*")]
    private static partial Regex SpacedBreak();
}
