using System.Text.Json;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
public sealed class EllaiDecisionTextStreamParserTests
{
    [Fact]
    public void EmitsOnlyCompleteDisplayTextSentencesAndIgnoresOtherJsonStrings()
    {
        const string displayText = """First sentence. Second has a "quoted phrase"! Final thought""";
        var response = JsonSerializer.Serialize(new
        {
            final_decision = new
            {
                displayText,
                spokenText = "not the visible answer",
            },
        });
        var parser = new EllaiDecisionTextStreamParser();
        var sentences = new List<string>();
        for (var offset = 0; offset < response.Length; offset += 7)
        {
            sentences.AddRange(parser.Append(response.Substring(offset, Math.Min(7, response.Length - offset))));
        }

        Assert.Equal(["First sentence.", "Second has a \"quoted phrase\"!"], sentences);
        Assert.Equal("Final thought", parser.FlushVerified(displayText));
    }

    [Fact]
    public void DoesNotExposeToolCallTextOrIncompleteSentences()
    {
        var parser = new EllaiDecisionTextStreamParser();
        Assert.Empty(parser.Append("{\"tool_calls\":[{\"name\":\"ellai_state\"}]}"));
        Assert.Empty(parser.Append("{\"final_decision\":{\"displayText\":\"Still talking"));
        Assert.Equal("Still talking", parser.FlushVerified("Still talking"));
    }
}
