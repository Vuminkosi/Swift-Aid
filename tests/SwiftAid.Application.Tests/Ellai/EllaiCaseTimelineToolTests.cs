using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiCaseTimelineToolTests
{
    [Fact]
    public async Task AuthorizedTimelineIncludesOnlyBoundedHistoryAndCaseVersionEvidence()
    {
        var id = Guid.NewGuid();
        var query = new TimelineQuery(id, true);
        var result = await Broker(query).InvokeAsync(Context(), Request(id), default);
        Assert.True(result.Allowed);
        Assert.Contains("Assignment acknowledged", result.ResultSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-actor", result.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("2 open escalations; 1 scheduled or acknowledged follow-ups", result.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("not established as linked", result.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("officer assignment are not available", result.ResultSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("derived receipt", result.ResultSummary, StringComparison.Ordinal);
        Assert.Equal("7", Assert.Single(result.Evidence).Version);
        Assert.NotNull(result.HistoryValidUntilUtc);
        Assert.Equal(1, query.Calls);
        var observation = EllaiObservedToolResult.From(Request(id), result);
        Assert.True(EllaiToolTrace.IsValid(EllaiToolTrace.Serialize([observation], false)!));
    }

    [Fact]
    public async Task TimelineLabelsEachAgentRoleWithoutInventingAQualityAssessment()
    {
        var id = Guid.NewGuid();
        var result = await Broker(new TimelineQuery(id, true, multiRole: true)).InvokeAsync(Context(), Request(id), default);
        Assert.True(result.Allowed);
        Assert.Contains("Ellai Follow-up: Overdue follow-up review recommended", result.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("Ellai Agent: Activity recorded", result.ResultSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("Data Quality", result.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeniedOrMissingActorDoesNotExposeTimeline()
    {
        var id = Guid.NewGuid();
        var query = new TimelineQuery(id, false);
        var denied = await Broker(query).InvokeAsync(Context(), Request(id), default);
        Assert.False(denied.Allowed);
        Assert.Empty(denied.Evidence);
        var noActor = await Broker(query).InvokeAsync(Context() with { Actor = null }, Request(id), default);
        Assert.False(noActor.Allowed);
        Assert.Equal(1, query.Calls);
    }

    private static EllaiToolBroker Broker(ICaseTimelineQuery query)
    {
        var consent = new EllaiConsentService();
        consent.Grant("session", EllaiConsentDimension.ReasoningText, EllaiToolBroker.ToolReadPurpose,
            EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromMinutes(5));
        return new EllaiToolBroker(new EllaiPolicyService(), consent, timeline: query);
    }

    private static EllaiToolContext Context()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "officer"),
            new Claim(ActorContext.AccessEpochClaimType, "1")], "fixture"));
        return new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo,
            Actor: ActorContext.FromValidatedPrincipal(principal),
            AllowedTools: new HashSet<string> { EllaiToolNames.CaseTimeline });
    }

    private static EllaiToolRequest Request(Guid id) =>
        new(EllaiToolNames.CaseTimeline, new Dictionary<string, string> { ["case_id"] = id.ToString("D") });

    private sealed class TimelineQuery(Guid id, bool allowed, bool multiRole = false) : ICaseTimelineQuery
    {
        public int Calls { get; private set; }
        public Task<CaseTimelineResult> GetAsync(ActorContext actor, Guid caseId, int? limit,
            CancellationToken ct, long? beforeAggregateVersion = null)
        {
            Calls++;
            Assert.Equal(id, caseId);
            Assert.Equal(20, limit);
            if (!allowed) return Task.FromResult(new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null));
            var now = DateTimeOffset.UtcNow;
            var header = new CaseTimelineHeader(id, "org", "workspace", "SZ", "Active", "secret-actor", 7, 1, now);
            var entries = new[]
            {
                new CaseTimelineEntry(Guid.Empty, "IntakePromoted", "derived receipt", CaseTimelineEntryKind.Derived, 0, 1, Guid.Empty, "secret-actor", null, now, 1),
                new CaseTimelineEntry(Guid.NewGuid(), "CaseAssignmentAcknowledged", "Assignment acknowledged", CaseTimelineEntryKind.Authoritative, 7, 1, Guid.NewGuid(), "secret-actor", null, now, 1)
                {
                    AgentActivities = multiRole
                        ? [new CaseAgentActivity(Guid.NewGuid(), "ops.followup", "ignored", "1", now),
                           new CaseAgentActivity(Guid.NewGuid(), "unknown", "ignored", "1", now)] : []
                }
            };
            return Task.FromResult(new CaseTimelineResult(CaseTimelineAccess.Found, new CaseTimeline(header, entries) { WorkSummary = new CaseTimelineWorkSummary(2, 1, now.AddDays(1)) }));
        }
    }
}
