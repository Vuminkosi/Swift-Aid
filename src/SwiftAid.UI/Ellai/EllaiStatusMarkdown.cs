using System.Text;

namespace SwiftAid.UI.Ellai;

/// <summary>Rich status content nodes. The parser accepts a small markdown
/// subset (bold, italic, code, links, lists, headings, line breaks) and the
/// renderer emits Blazor elements only — raw HTML is never passed through,
/// and link targets are restricted to http/https.</summary>
public abstract record EllaiStatusNode
{
    public sealed record Paragraph(IReadOnlyList<EllaiStatusNode> Inline) : EllaiStatusNode;

    public sealed record BulletList(IReadOnlyList<IReadOnlyList<EllaiStatusNode>> Items) : EllaiStatusNode;

    public sealed record Text(string Value) : EllaiStatusNode;

    public sealed record Bold(string Value) : EllaiStatusNode;

    public sealed record Italic(string Value) : EllaiStatusNode;

    public sealed record Code(string Value) : EllaiStatusNode;

    public sealed record Link(string Label, string Url) : EllaiStatusNode;

    public sealed record LineBreak : EllaiStatusNode
    {
        public static readonly LineBreak Instance = new();
    }
}

public static class EllaiStatusMarkdown
{
    public static IReadOnlyList<EllaiStatusNode> ParseBlocks(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return Array.Empty<EllaiStatusNode>();
        }

        var blocks = new List<EllaiStatusNode>();
        List<IReadOnlyList<EllaiStatusNode>>? list = null;

        void FlushList()
        {
            if (list is not null)
            {
                blocks.Add(new EllaiStatusNode.BulletList(list));
                list = null;
            }
        }

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                FlushList();
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                list ??= new List<IReadOnlyList<EllaiStatusNode>>();
                list.Add(ParseInline(line[2..]));
                continue;
            }

            FlushList();
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                blocks.Add(new EllaiStatusNode.Paragraph(
                    new[] { new EllaiStatusNode.Bold(line[2..].Trim()) }));
                continue;
            }

            blocks.Add(new EllaiStatusNode.Paragraph(ParseInline(line)));
        }

        FlushList();
        return blocks;
    }

    public static IReadOnlyList<EllaiStatusNode> ParseInline(string text)
    {
        var nodes = new List<EllaiStatusNode>();
        var literal = new StringBuilder();
        var i = 0;

        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                nodes.Add(new EllaiStatusNode.Text(literal.ToString()));
                literal.Clear();
            }
        }

        while (i < text.Length)
        {
            if (Match(text, i, "**", out var boldEnd) && boldEnd > i + 2)
            {
                FlushLiteral();
                nodes.Add(new EllaiStatusNode.Bold(text.Substring(i + 2, boldEnd - i - 2)));
                i = boldEnd + 2;
            }
            else if (text[i] == '*' && FindClosing(text, i + 1, '*', out var italicEnd) && italicEnd > i + 1)
            {
                FlushLiteral();
                nodes.Add(new EllaiStatusNode.Italic(text.Substring(i + 1, italicEnd - i - 1)));
                i = italicEnd + 1;
            }
            else if (text[i] == '`' && FindClosing(text, i + 1, '`', out var codeEnd) && codeEnd > i + 1)
            {
                FlushLiteral();
                nodes.Add(new EllaiStatusNode.Code(text.Substring(i + 1, codeEnd - i - 1)));
                i = codeEnd + 1;
            }
            else if (text[i] == '[' && TryParseLink(text, i, out var link, out var next))
            {
                FlushLiteral();
                nodes.Add(link);
                i = next;
            }
            else
            {
                literal.Append(text[i]);
                i++;
            }
        }

        FlushLiteral();
        return nodes;
    }

    public static bool IsSafeWebUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public static bool IsSafeMediaUrl(string? url) =>
        IsSafeWebUrl(url) || (!string.IsNullOrWhiteSpace(url) && url.StartsWith("/", StringComparison.Ordinal));

    private static bool Match(string text, int index, string marker, out int closing)
    {
        closing = -1;
        if (!text.AsSpan(index).StartsWith(marker, StringComparison.Ordinal))
        {
            return false;
        }

        var end = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        closing = end;
        return true;
    }

    private static bool FindClosing(string text, int start, char marker, out int closing)
    {
        closing = text.IndexOf(marker, start);
        return closing >= 0;
    }

    private static bool TryParseLink(string text, int index, out EllaiStatusNode link, out int next)
    {
        link = new EllaiStatusNode.Text("[");
        next = index + 1;
        var closeText = text.IndexOf(']', index + 1);
        if (closeText < 0 || closeText + 1 >= text.Length || text[closeText + 1] != '(')
        {
            return false;
        }

        var closeUrl = text.IndexOf(')', closeText + 2);
        if (closeUrl < 0)
        {
            return false;
        }

        var label = text.Substring(index + 1, closeText - index - 1);
        var url = text.Substring(closeText + 2, closeUrl - closeText - 2).Trim();
        next = closeUrl + 1;
        if (string.IsNullOrWhiteSpace(label) || !IsSafeWebUrl(url))
        {
            link = new EllaiStatusNode.Text(text.Substring(index, next - index));
            return true;
        }

        link = new EllaiStatusNode.Link(label, url);
        return true;
    }
}
