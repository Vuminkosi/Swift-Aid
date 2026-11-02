using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T06")]
public sealed class EllaiModelTurnParserTests
{
    [Theory]
    [InlineData("Unknown tool 'private-model-value'.", "unregistered-tool")]
    [InlineData("Tool call 'private-id' argument 'private-field' must be a string.", "tool-argument-type")]
    [InlineData("The reply must contain exactly one of tool_calls or final_decision.", "turn-envelope")]
    [InlineData("The reply was not a JSON object.", "invalid-json")]
    public void DiagnosticsAreBoundedAndDoNotEchoProviderValues(string error, string expected)
    {
        Assert.Equal(expected, EllaiModelTurnParser.DiagnosticCode(error));
    }

    private const string DecisionJson = """
        {"schemaVersion":2,"kind":"Answer","displayText":"Queue reviewed.","spokenText":"Done.",
         "claims":[{"claimId":"c1","text":"Queue has 3 items.","kind":"Observed",
           "evidence":[{"sourceKind":"queue","resourceId":"q1","version":"v3","fieldPath":null,"evidenceId":"e1"}],
           "factualRationale":"Seen."}],
         "questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],
         "resumeSuggestion":null,"stopReason":null}
        """;

    [Fact]
    public void ToolCallCountUsesTheFourThousandHardLimit()
    {
        var calls = Enumerable.Range(0, 4_001).Select(index =>
            new { id = $"call-{index}", name = "ellai_now", args = new Dictionary<string, string>() }).ToArray();
        var accepted = EllaiModelTurnParser.Parse(System.Text.Json.JsonSerializer.Serialize(
            new { tool_calls = calls.Take(4_000) }));
        Assert.Null(accepted.Error);
        Assert.Equal(4_000, accepted.Turn!.ToolRequests.Count);
        var rejected = EllaiModelTurnParser.Parse(System.Text.Json.JsonSerializer.Serialize(new { tool_calls = calls }));
        Assert.NotNull(rejected.Error);
    }

    [Fact]
    public void ToolCallsParseWithCallIds()
    {
        var parsed = EllaiModelTurnParser.Parse(
            """{"tool_calls":[{"id":"c1","name":"ellai_state","args":{"projection":"queue"}},{"id":"c2","name":"ellai_now","args":{}}]}""");

        Assert.Null(parsed.Error);
        Assert.NotNull(parsed.Turn);
        Assert.Null(parsed.Turn.FinalDecision);
        Assert.Equal("c1", parsed.Turn.ToolRequests[0].CallId);
        Assert.Equal(EllaiToolNames.State, parsed.Turn.ToolRequests[0].ToolName);
        Assert.Equal("queue", parsed.Turn.ToolRequests[0].Arguments["projection"]);
        Assert.Equal("c2", parsed.Turn.ToolRequests[1].CallId);
    }

    [Fact]
    public void AliasesResolveToCanonical()
    {
        var parsed = EllaiModelTurnParser.Parse(
            """{"tool_calls":[{"id":"c1","name":"ellai_guidance_session","args":{}}]}""");

        Assert.Null(parsed.Error);
        Assert.Equal(EllaiToolNames.State, parsed.Turn!.ToolRequests[0].ToolName);
        Assert.Equal("c1", parsed.Turn.ToolRequests[0].CallId);
    }

    [Fact]
    public void FinalDecisionParsesWithStringEnums()
    {
        var parsed = EllaiModelTurnParser.Parse($"{{\"final_decision\":{DecisionJson}}}");

        Assert.Null(parsed.Error);
        Assert.NotNull(parsed.Turn);
        Assert.Empty(parsed.Turn.ToolRequests);
        Assert.NotNull(parsed.Turn.FinalDecision);
        Assert.Equal(DecisionKind.Answer, parsed.Turn.FinalDecision.Kind);
        Assert.Equal("Queue reviewed.", parsed.Turn.FinalDecision.DisplayText);
    }

    [Fact]
    public void FencedJsonIsAcceptedDeterministically()
    {
        var parsed = EllaiModelTurnParser.Parse(
            "```json\n" +
            """{"tool_calls":[{"id":"c1","name":"ellai_now","args":{}}]}""" +
            "\n```");

        Assert.Null(parsed.Error);
        Assert.Equal("c1", parsed.Turn!.ToolRequests[0].CallId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("just some prose")]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    [InlineData("""{"tool_calls":[],"final_decision":null}""")]
    [InlineData("""{"tool_calls":[{"id":"c1","name":"ellai_sql","args":{}}]}""")]
    [InlineData("""{"tool_calls":[{"id":"c1","name":"ellai_state","args":{"limit":5}}]}""")]
    [InlineData("""{"tool_calls":[{"id":"c1","name":"ellai_state"},{"id":"c1","name":"ellai_now","args":{}}]}""")]
    public void MalformedTurnsAreRepairableErrors(string text)
    {
        var parsed = EllaiModelTurnParser.Parse(text);

        Assert.Null(parsed.Turn);
        Assert.False(string.IsNullOrWhiteSpace(parsed.Error));
    }

    [Fact]
    public void ThinDecisionParsesButFailsValidation()
    {
        var parsed = EllaiModelTurnParser.Parse("""{"final_decision":{"kind":"Answer"}}""");

        Assert.Null(parsed.Error);
        Assert.NotNull(parsed.Turn?.FinalDecision);
        Assert.NotEmpty(EllaiDecisionValidator.Validate(parsed.Turn.FinalDecision));
    }

    [Fact]
    public void UnknownToolNameMatchesBrokerRegistry()
    {
        Assert.False(EllaiToolBroker.TryResolveCanonical("ellai_sql", out _));
        var parsed = EllaiModelTurnParser.Parse(
            """{"tool_calls":[{"id":"c1","name":"ellai_sql","args":{}}]}""");

        Assert.Null(parsed.Turn);
        Assert.Contains("ellai_sql", parsed.Error, StringComparison.Ordinal);
    }
}
