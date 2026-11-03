using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Ellai;
using SwiftAid.Domain.Cases;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiOwnedCaseCountTests
{
    private static readonly ActorContext Actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "count-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
    private static readonly EllaiToolContext Context = new("count-session", "scope", "cases", null, 4, true,
        "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive, Actor,
        new HashSet<string> { EllaiToolNames.OwnedCaseCount }, EllaiConsentPurposes.PublicProductHelp);
    private static readonly DateTimeOffset AsOf = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CountUsesAuthorizedQueryAndLabelsWindowScopeAndEvidence()
    {
        var rows = new[] { Row(), Row() };
        var query = new FixtureQuery(new(DashboardAggregateAccess.Allowed, rows, AsOf, false));
        var result = await Broker(query).InvokeAsync(Context, Request("30"), default);
        Assert.True(result.Allowed);
        Assert.Same(Actor, query.Actor);
        Assert.Equal(TimeSpan.FromDays(30), query.Window);
        Assert.Contains("2 managed case(s)", result.ResultSummary);
        Assert.Contains("30-day lookback", result.ResultSummary);
        Assert.Contains("currently owned", result.ResultSummary);
        Assert.Contains(AsOf.ToString("O"), result.ResultSummary);
        Assert.DoesNotContain(rows[0].CaseId.ToString(), result.ResultSummary);
        Assert.Equal("DashboardAggregate", Assert.Single(result.Evidence).SourceKind);
    }

    [Theory]
    [InlineData(DashboardAggregateAccess.Denied, false)]
    [InlineData(DashboardAggregateAccess.Unavailable, false)]
    [InlineData(DashboardAggregateAccess.Allowed, true)]
    public async Task DeniedUnavailableOrTruncatedCountsNeverExposePartialResults(DashboardAggregateAccess access, bool truncated)
    {
        var result = await Broker(new FixtureQuery(new(access, [Row()], AsOf, truncated))).InvokeAsync(Context, Request("30"), default);
        Assert.False(result.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, result.ReasonCode);
        Assert.Empty(result.Evidence);
        Assert.DoesNotContain("1 managed case", result.ResultSummary);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("366")]
    [InlineData("01")]
    [InlineData("-1")]
    public async Task InvalidWindowFailsBeforeQuery(string days)
    {
        var query = new FixtureQuery(new(DashboardAggregateAccess.Allowed, [], AsOf, false));
        var result = await Broker(query).InvokeAsync(Context, Request(days), default);
        Assert.False(result.Allowed);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, result.ReasonCode);
        Assert.Null(query.Actor);
    }

    [Fact]
    public async Task GuestProfileCannotInvokeCountAndMissingActorFailsClosed()
    {
        var query = new FixtureQuery(new(DashboardAggregateAccess.Allowed, [Row()], AsOf, false));
        var guest = await Broker(query).InvokeAsync(Context with { AllowedTools = new HashSet<string>() }, Request("30"), default);
        Assert.False(guest.Allowed);
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, guest.ReasonCode);
        var noActor = await Broker(query).InvokeAsync(Context with { Actor = null }, Request("30"), default);
        Assert.False(noActor.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, noActor.ReasonCode);
        Assert.Null(query.Actor);
    }

    [Fact]
    public async Task QueryFailureIsSanitizedAndCancellationPropagates()
    {
        var failure = await Broker(new FailingQuery()).InvokeAsync(Context, Request("30"), default);
        Assert.False(failure.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, failure.ReasonCode);
        Assert.DoesNotContain("private fixture detail", failure.DenialReason);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Broker(new FailingQuery())
            .InvokeAsync(Context, Request("30"), cancelled.Token));
    }

    private static EllaiToolBroker Broker(IDashboardAggregateQuery query) =>
        new(new EllaiPolicyService(), new EllaiConsentService(), dashboard: query);
    private static EllaiToolRequest Request(string days) => new(EllaiToolNames.OwnedCaseCount,
        new Dictionary<string, string> { ["window_days"] = days });
    private static DashboardAggregateCaseRow Row() => new(Guid.NewGuid(), Sector.HumanHealth,
        ClinicalClassification.Unclassified, CaseWorkflow.Reported, "SZ", [], AsOf.AddDays(-1), AsOf);

    private sealed class FixtureQuery(DashboardAggregateResult result) : IDashboardAggregateQuery
    {
        public ActorContext? Actor { get; private set; }
        public TimeSpan Window { get; private set; }
        public Task<DashboardAggregateResult> GetAsync(ActorContext actor, TimeSpan window, CancellationToken ct)
        {
            Actor = actor;
            Window = window;
            return Task.FromResult(result);
        }
    }

    private sealed class FailingQuery : IDashboardAggregateQuery
    {
        public Task<DashboardAggregateResult> GetAsync(ActorContext actor, TimeSpan window, CancellationToken ct) =>
            throw new InvalidOperationException("private fixture detail");
    }
}
