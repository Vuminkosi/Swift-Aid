using SwiftAid.UI.Ellai;

namespace SwiftAid.Ui.Tests;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T13")]
public sealed class EllaiStatusMarkdownTests
{
    [Fact]
    public void InlineElementsParse()
    {
        var nodes = EllaiStatusMarkdown.ParseInline("See **queue** and *filter* plus `code`.");

        Assert.Collection(nodes,
            node => Assert.Equal("See ", ((EllaiStatusNode.Text)node).Value),
            node => Assert.Equal("queue", ((EllaiStatusNode.Bold)node).Value),
            node => Assert.Equal(" and ", ((EllaiStatusNode.Text)node).Value),
            node => Assert.Equal("filter", ((EllaiStatusNode.Italic)node).Value),
            node => Assert.Equal(" plus ", ((EllaiStatusNode.Text)node).Value),
            node => Assert.Equal("code", ((EllaiStatusNode.Code)node).Value),
            node => Assert.Equal(".", ((EllaiStatusNode.Text)node).Value));
    }

    [Fact]
    public void SafeLinksParseAndUnsafeTargetsStayLiteral()
    {
        var safe = EllaiStatusMarkdown.ParseInline("[Guide](https://example.com/help)");
        var link = Assert.Single(safe);
        var parsed = Assert.IsType<EllaiStatusNode.Link>(link);
        Assert.Equal("Guide", parsed.Label);
        Assert.Equal("https://example.com/help", parsed.Url);

        var unsafeLink = EllaiStatusMarkdown.ParseInline("[X](javascript:runthis)");
        var literal = Assert.Single(unsafeLink);
        Assert.IsType<EllaiStatusNode.Text>(literal);
    }

    [Fact]
    public void UnclosedMarkersStayLiteral()
    {
        var nodes = EllaiStatusMarkdown.ParseInline("An **open marker and `code");
        var text = Assert.Single(nodes);
        Assert.Equal("An **open marker and `code", ((EllaiStatusNode.Text)text).Value);
    }

    [Fact]
    public void BlocksSplitListsAndParagraphs()
    {
        var blocks = EllaiStatusMarkdown.ParseBlocks("First line\n\n- one\n- two\n\nLast **line**");

        Assert.Equal(3, blocks.Count);
        Assert.IsType<EllaiStatusNode.Paragraph>(blocks[0]);
        var list = Assert.IsType<EllaiStatusNode.BulletList>(blocks[1]);
        Assert.Equal(2, list.Items.Count);
        var last = Assert.IsType<EllaiStatusNode.Paragraph>(blocks[2]);
        Assert.Contains(last.Inline, node => node is EllaiStatusNode.Bold);
    }

    [Theory]
    [InlineData("https://files.example/voice.mp3", true)]
    [InlineData("http://files.example/voice.mp3", true)]
    [InlineData("/media/voice.mp3", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,<b>x</b>", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void MediaUrlsAreAllowlisted(string? url, bool expected)
    {
        Assert.Equal(expected, EllaiStatusMarkdown.IsSafeMediaUrl(url));
    }
}
