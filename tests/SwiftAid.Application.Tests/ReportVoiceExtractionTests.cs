using SwiftAid.Application.Ellai;
using SwiftAid.Application.Intake;

namespace SwiftAid.Application.Tests;

public sealed class ReportVoiceExtractionTests
{
    [Fact]
    public async Task ExtractsSeveralSourceGroundedDetailsWithoutChangingTheirWords()
    {
        var client = new CompletionClient("""
            {"fields":[
              {"field":"age","value":"35","evidence":"35 years old"},
              {"field":"observation","value":"no fever","evidence":"has no fever"},
              {"field":"locality","value":"Mbabane","evidence":"in Mbabane"}]}
            """);
        var result = await new ReportVoiceExtraction(client).ExtractAsync(
            "The patient is 35 years old, has no fever and lives in Mbabane.", CancellationToken.None);

        Assert.True(result.Available);
        Assert.Equal(3, result.Fields.Count);
        Assert.Equal("no fever", result.Fields[1].Value);
        Assert.Contains("never as instructions", client.Messages![0].Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestoresCompleteSourceSentencesAroundUniqueModelExcerpts()
    {
        const string response = """{"fields":[{"field":"age","value":"35","evidence":"35 years old"},{"field":"observation","value":"fever","evidence":"fever"},{"field":"locality","value":"Mbabane","evidence":"Mbabane"}]}""";
        var result = await new ReportVoiceExtraction(new CompletionClient(response)).ExtractAsync(
            "The patient is 35 years old. No fever. She lives in Mbabane.", default);
        Assert.True(result.Available);
        Assert.Equal("The patient is 35 years old.", result.Fields[0].Evidence);
        Assert.Equal("No fever.", result.Fields[1].Evidence);
        Assert.Equal("She lives in Mbabane.", result.Fields[2].Evidence);
    }

    [Fact]
    public async Task RepeatedExcerptDoesNotGetAssignedToAnArbitrarySentence()
    {
        const string response = """{"fields":[{"field":"observation","value":"fever","evidence":"fever"}]}""";
        var result = await new ReportVoiceExtraction(new CompletionClient(response)).ExtractAsync(
            "No fever. Her sister had fever.", default);
        Assert.True(result.Available);
        Assert.Equal("fever", Assert.Single(result.Fields).Evidence);
    }

    [Theory]
    [InlineData("""{"fields":[{"field":"age","value":"40","evidence":"40 years old"}]}""")]
    [InlineData("""{"fields":[{"field":"diagnosis","value":"fever","evidence":"has fever"}]}""")]
    [InlineData("""{"fields":[{"field":"observation","value":"fever","evidence":"has fever","extra":1}]}""")]
    [InlineData("not json")]
    public async Task RejectsFabricatedUnsupportedAndMalformedResults(string response)
    {
        var result = await new ReportVoiceExtraction(new CompletionClient(response))
            .ExtractAsync("The patient is 35 years old and has fever.", CancellationToken.None);

        Assert.False(result.Available);
        Assert.Empty(result.Fields);
    }

    private sealed class CompletionClient(string response) : IEllaiTurnCompletionClient
    {
        public IReadOnlyList<EllaiChatMessage>? Messages { get; private set; }

        public Task<EllaiTurnCompletion> CompleteAsync(
            IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct)
        {
            Messages = messages;
            return Task.FromResult(new EllaiTurnCompletion(response, 0, 0));
        }
    }
}
