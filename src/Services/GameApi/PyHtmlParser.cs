using System.IO;
using System.Text.RegularExpressions;

namespace DeadlockAdvisor.Services.GameApi;

/// <summary>
/// Python 3.12's <c>html.parser.HTMLParser</c> with <c>convert_charrefs=True</c>, ported state for
/// state: the game sync turns the API's item descriptions into tooltip text with it, and the file
/// it writes has to match the Python app's byte for byte, malformed markup included (an unclosed
/// tag becomes text, a stray <c>&lt;</c> is data, and so on). Feed, then Close.
/// </summary>
public abstract partial class PyHtmlParser
{
    private static readonly string[] _cdataContentElements = ["script", "style"];

    private string _raw = "";
    private string? _cdataElem;
    private Regex? _cdataEnd;

    public void Feed(string data)
    {
        _raw += data;
        GoAhead(end: false);
    }

    public void Close() => GoAhead(end: true);

    protected virtual void HandleStartTag(string tag, IReadOnlyList<(string Name, string? Value)> attrs)
    {
    }

    protected virtual void HandleStartEndTag(string tag, IReadOnlyList<(string Name, string? Value)> attrs)
    {
        HandleStartTag(tag, attrs);
        HandleEndTag(tag);
    }

    protected virtual void HandleEndTag(string tag)
    {
    }

    protected virtual void HandleData(string data)
    {
    }

    private void GoAhead(bool end)
    {
        var raw = _raw;
        var i = 0;
        var n = raw.Length;
        while (i < n)
        {
            int j;
            if (_cdataElem is null)
            {
                j = raw.IndexOf('<', i);
                if (j < 0)
                {
                    // A charref may be cut in half at the end of what's been fed: wait for the rest.
                    var from = Math.Max(i, n - 34);
                    var ampersand = raw.LastIndexOf('&', n - 1, n - from);
                    if (ampersand >= 0 && !SpaceOrSemicolon().Match(raw, ampersand).Success)
                        break;
                    j = n;
                }
            }
            else
            {
                var match = _cdataEnd!.Match(raw, i);
                if (!match.Success)
                    break;
                j = match.Index;
            }

            if (i < j)
                HandleData(_cdataElem is null ? PyText.Unescape(raw[i..j]) : raw[i..j]);
            i = j;
            if (i == n)
                break;

            int k;
            if (i + 1 < n && char.IsAsciiLetter(raw[i + 1]))
                k = ParseStartTag(i);
            else if (StartsWith(raw, "</", i))
                k = ParseEndTag(i);
            else if (StartsWith(raw, "<!--", i))
                k = ParseComment(i);
            else if (StartsWith(raw, "<?", i))
                k = ParsePi(i);
            else if (StartsWith(raw, "<!", i))
                k = ParseHtmlDeclaration(i);
            else if (i + 1 < n)
            {
                HandleData("<");
                k = i + 1;
            }
            else
                break;

            if (k < 0)
            {
                if (!end)
                    break;
                // At the end of input an unfinished construct is just text, up to the next > or <.
                k = raw.IndexOf('>', i + 1);
                if (k < 0)
                {
                    k = raw.IndexOf('<', i + 1);
                    if (k < 0)
                        k = i + 1;
                }
                else
                {
                    k += 1;
                }
                HandleData(_cdataElem is null ? PyText.Unescape(raw[i..k]) : raw[i..k]);
            }
            i = k;
        }

        if (end && i < n && _cdataElem is null)
        {
            HandleData(PyText.Unescape(raw[i..n]));
            i = n;
        }
        _raw = raw[i..];
    }

    private static bool StartsWith(string text, string prefix, int at) =>
        string.CompareOrdinal(text, at, prefix, 0, prefix.Length) == 0;

    private int ParseHtmlDeclaration(int i)
    {
        if (StartsWith(_raw, "<!--", i))
            return ParseComment(i);
        if (StartsWith(_raw, "<![", i))
            return ParseMarkedSection(i);
        if (i + 9 <= _raw.Length && _raw.Substring(i, 9).Equals("<!doctype", StringComparison.OrdinalIgnoreCase))
        {
            var gt = _raw.IndexOf('>', i + 9);
            return gt < 0 ? -1 : gt + 1;
        }
        return ParseBogusComment(i);
    }

    private int ParseBogusComment(int i)
    {
        var pos = _raw.IndexOf('>', i + 2);
        return pos < 0 ? -1 : pos + 1;
    }

    private int ParseComment(int i)
    {
        var match = CommentClose().Match(_raw, i + 4);
        return match.Success ? match.Index + match.Length : -1;
    }

    private int ParsePi(int i)
    {
        var gt = _raw.IndexOf('>', i + 2);
        return gt < 0 ? -1 : gt + 1;
    }

    /// <summary>_markupbase's marked sections: &lt;![CDATA[...]]&gt; and MS Office's &lt;![if ...]&gt;. Anything else fails, as in Python.</summary>
    private int ParseMarkedSection(int i)
    {
        var start = i + 3;
        if (start == _raw.Length)
            return -1;
        var name = DeclName().Match(_raw, start);
        if (!name.Success)
            throw new InvalidDataException($"expected name token at '{_raw[i..Math.Min(_raw.Length, i + 20)]}'");
        if (start + name.Length == _raw.Length)
            return -1;

        var section = PyText.Strip(name.Value).ToLowerInvariant();
        Match close;
        if (section is "temp" or "cdata" or "ignore" or "include" or "rcdata")
            close = MarkedSectionClose().Match(_raw, start);
        else if (section is "if" or "else" or "endif")
            close = MsMarkedSectionClose().Match(_raw, start);
        else
            throw new InvalidDataException($"unknown status keyword '{section}' in marked section");
        return close.Success ? close.Index + close.Length : -1;
    }

    private int ParseStartTag(int i)
    {
        var endPos = CheckForWholeStartTag(i);
        if (endPos < 0)
            return endPos;

        var tagMatch = TagFindTolerant().Match(_raw, i + 1);
        var k = tagMatch.Index + tagMatch.Length;
        var tag = tagMatch.Groups[1].Value.ToLowerInvariant();
        var attrs = new List<(string, string?)>();
        while (k < endPos)
        {
            var attr = AttrFindTolerant().Match(_raw, k);
            if (!attr.Success)
                break;
            string? value = null;
            if (attr.Groups[2].Success && attr.Groups[2].Length > 0)
            {
                value = attr.Groups[3].Value;
                if (value.Length >= 2 && (value[0] == '\'' && value[^1] == '\'' || value[0] == '"' && value[^1] == '"'))
                    value = value[1..^1];
                else if (value is "'" or "\"")
                    value = "";
            }
            if (!string.IsNullOrEmpty(value))
                value = PyText.Unescape(value);
            attrs.Add((attr.Groups[1].Value.ToLowerInvariant(), value));
            k = attr.Index + attr.Length;
        }

        var endText = PyText.Strip(_raw[k..endPos]);
        if (endText is not (">" or "/>"))
        {
            HandleData(_raw[i..endPos]);
            return endPos;
        }
        if (endText.EndsWith("/>", StringComparison.Ordinal))
        {
            HandleStartEndTag(tag, attrs);
        }
        else
        {
            HandleStartTag(tag, attrs);
            if (_cdataContentElements.Contains(tag))
                SetCdataMode(tag);
        }
        return endPos;
    }

    /// <summary>Where the start tag at <paramref name="i"/> ends, or -1 if it runs off the end of the input.</summary>
    private int CheckForWholeStartTag(int i)
    {
        var match = LocateStartTagEnd().Match(_raw, i);
        var j = match.Index + match.Length;
        if (j == _raw.Length)
            return -1;
        var next = _raw[j];
        if (next == '>')
            return j + 1;
        if (next == '/')
            return StartsWith(_raw, "/>", j) ? j + 2 : -1;
        if (char.IsAsciiLetter(next) || next == '=')
            return -1;
        return j > i ? j : i + 1;
    }

    private int ParseEndTag(int i)
    {
        var gt = _raw.IndexOf('>', i + 1);
        if (gt < 0)
            return -1;
        var gtPos = gt + 1;

        var match = EndTagFind().Match(_raw, i);
        if (!match.Success)
        {
            if (_cdataElem is not null)
            {
                HandleData(_raw[i..gtPos]);
                return gtPos;
            }
            var name = TagFindTolerant().Match(_raw, i + 2);
            if (!name.Success)
                return StartsWith(_raw, "</>", i) ? i + 3 : ParseBogusComment(i);
            var tagName = name.Groups[1].Value.ToLowerInvariant();
            gtPos = _raw.IndexOf('>', name.Index + name.Length);
            HandleEndTag(tagName);
            return gtPos + 1;
        }

        var element = match.Groups[1].Value.ToLowerInvariant();
        if (_cdataElem is not null && element != _cdataElem)
        {
            HandleData(_raw[i..gtPos]);
            return gtPos;
        }
        HandleEndTag(element);
        _cdataElem = null;
        _cdataEnd = null;
        return gtPos;
    }

    private void SetCdataMode(string element)
    {
        _cdataElem = element;
        _cdataEnd = new Regex(@"</" + PyText.Space + "*" + Regex.Escape(element) + PyText.Space + "*>", RegexOptions.IgnoreCase);
    }

    // Python's patterns, anchored with \G for re.match at a position; \s widened to Python's
    // whitespace (U+001C-U+001F included).
    private const string S = @"\s\x1c-\x1f";

    [GeneratedRegex(@"[" + S + ";]")]
    private static partial Regex SpaceOrSemicolon();

    [GeneratedRegex(@"--[" + S + "]*>")]
    private static partial Regex CommentClose();

    [GeneratedRegex(@"\G[a-zA-Z][-_.a-zA-Z0-9]*[" + S + "]*")]
    private static partial Regex DeclName();

    [GeneratedRegex(@"][" + S + "]*][" + S + "]*>")]
    private static partial Regex MarkedSectionClose();

    [GeneratedRegex(@"][" + S + "]*>")]
    private static partial Regex MsMarkedSectionClose();

    [GeneratedRegex(@"\G([a-zA-Z][^\t\n\r\f />\x00]*)(?:[" + S + "]|/(?!>))*")]
    private static partial Regex TagFindTolerant();

    [GeneratedRegex(@"\G((?<=['""" + S + "/])[^" + S + "/>][^" + S + "/=>]*)([" + S + "]*=+[" + S + "]*('[^']*'|\"[^\"]*\"|(?!['\"])[^>" + S + "]*))?(?:[" + S + "]|/(?!>))*")]
    private static partial Regex AttrFindTolerant();

    [GeneratedRegex(@"\G<[a-zA-Z][^\t\n\r\f />\x00]*(?:[" + S + "/]*(?:(?<=['\"" + S + "/])[^" + S + "/>][^" + S + "/=>]*(?:[" + S + "]*=+[" + S + "]*(?:'[^']*'|\"[^\"]*\"|(?!['\"])[^>" + S + "]*)[" + S + "]*)?(?:[" + S + "]|/(?!>))*)*)?[" + S + "]*")]
    private static partial Regex LocateStartTagEnd();

    [GeneratedRegex(@"\G</[" + S + @"]*([a-zA-Z][-.a-zA-Z0-9:_]*)[" + S + "]*>")]
    private static partial Regex EndTagFind();
}
