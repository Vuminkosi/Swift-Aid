using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Ellai;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiCaseSummaryReplayTests
{
    [Fact]
    public async Task LongReportedPhrasesKeepTheAnswerInsideSpeechAndDisplayBounds()
    {
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "summary-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "fixture")));
        var consent = new EllaiConsentService();
        consent.Grant("summary-session", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        var context = new EllaiToolContext("summary-session", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo, Actor: actor);
        var data = new Data();
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, queue: data, caseReports: data,
            locationReplayProtector: new EllaiLocationReplayProtector(new EphemeralDataProtectionProvider()));
        var result = await broker.InvokeAsync(context, new(EllaiToolNames.CaseSummary,
            new Dictionary<string, string> { ["field"] = "observations" }), default);
        Assert.True(result.Allowed);
        Assert.True(result.ResultSummary.Length <= EllaiDecisionValidator.MaxDisplayChars);
        Assert.True(result.ResultSummary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= EllaiDecisionValidator.MaxSpokenWords);
        Assert.Contains("Additional reported phrases omitted", result.ResultSummary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedSummaryIsRecomputedAndCannotReplayAfterAccessChanges(bool revoked)
    {
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "summary-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "fixture")));
        var protector = new EllaiLocationReplayProtector(new EphemeralDataProtectionProvider());
        var data = new Data();
        var consent = new EllaiConsentService();
        consent.Grant("summary-session", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        var context = new EllaiToolContext("summary-session", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo, Actor: actor);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent, queue: data,
            caseReports: data, locationReplayProtector: protector);
        var request = new EllaiToolRequest(EllaiToolNames.CaseSummary, new Dictionary<string, string>());
        var result = await broker.InvokeAsync(context, request, default);
        Assert.True(result.Allowed, result.DenialReason);
        Assert.Contains("1 reported case", result.ResultSummary);
        var observation = EllaiObservedToolResult.From(request, result);
        var trace = EllaiToolTrace.SerializeFlattened([observation], false, []);
        Assert.NotNull(trace);
        Assert.NotNull(observation.LocationReplayScope);
        data.Revoked = revoked;
        var turns = new[]
        {
            new EllaiConversationTurn(context.SessionId, "prior", 1, "How many cases?", result.ResultSummary, "Completed", 1, DateTimeOffset.UtcNow) { ToolTraceJson = trace },
            new EllaiConversationTurn(context.SessionId, "current", 2, "And now?", null, "Processing", 1, null)
        };
        var history = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", actor, data, default,
            toolContext: context, invokeTool: broker.InvokeAsync, locationReplayProtector: protector);
        Assert.Equal(!revoked, history.RetainedTurnIds.Contains("prior"));
        Assert.True(data.Reads >= 2);
    }

    private sealed class Data : ICaseQueueQuery, ICaseReportReadQuery
    {
        private readonly Guid _id = Guid.NewGuid();
        public bool Revoked { get; set; }
        public int Reads { get; private set; }
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<CaseQueueResult> GetSearchableAsync(ActorContext actor, int? limit, CaseQueueFilter? filter, string? cursor, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, Revoked ? [] :
                [new(_id, "Reported", "SZ", actor.ActorId, 2, DateTimeOffset.UnixEpoch, "Human", "Unclassified")]));
        }
        public Task<CaseReportDetails?> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct) =>
            Task.FromResult<CaseReportDetails?>(new(caseId, 1, null,
                Enumerable.Range(0, 10).Select(index => string.Join(" ", Enumerable.Repeat("word", 55)) + index).ToArray(),
                null, AggregateVersion: 2));
    }
}
