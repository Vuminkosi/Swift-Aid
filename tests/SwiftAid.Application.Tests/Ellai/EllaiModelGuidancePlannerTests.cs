using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiModelGuidancePlannerTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":1,\"status\":\"Ready\",\"objective\":\"help\",\"mode\":\"Navigation\",\"steps\":null,\"questions\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"status\":999,\"steps\":[],\"questions\":[]}")]
    [InlineData("{\"execute\":\"submit\"}")]
    public async Task Invalid_model_plans_fail_closed_and_retain_usage(string text)
    {
        var result = await new EllaiModelGuidancePlanner(new Completion(text)).ComposeAsync(
            "help", GuidanceMode.Navigation, new([], "catalogue", "ui", false), [], default);
        Assert.Null(result.Draft);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(17, result.Usage.PromptTokens);
    }

    [Fact]
    public async Task Clarification_is_generated_by_existing_completion_client()
    {
        var result = await new EllaiModelGuidancePlanner(new Completion("""
            {"schemaVersion":1,"status":"NeedsInput","objective":"help","mode":"Navigation",
             "steps":[],"questions":[{"questionId":"q1","text":"Which report?","choiceIds":[]}],"stopReason":null}
            """)).ComposeAsync("help", GuidanceMode.Navigation, new([], "catalogue", "ui", false), [], default);
        Assert.NotNull(result.Draft);
        Assert.Empty(result.Errors);
    }

    private sealed class Completion(string text) : IEllaiTurnCompletionClient
    {
        public Task<EllaiTurnCompletion> CompleteAsync(IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct)
        {
            Assert.Equal("system", messages[0].Role);
            Assert.Equal(2048, maxTokens);
            return Task.FromResult(new EllaiTurnCompletion(text, 17, 9));
        }
    }
}
