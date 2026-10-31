using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T08")]
public sealed class EllaiUtteranceSubmissionServiceTests
{
    [Theory]
    [InlineData("synthetic demo prompt")]
    [InlineData("case 4812, patient narrative and report details")]
    [InlineData("unclassifiable arbitrary text")]
    public async Task UnclassifiedFreeTextIsBlockedBeforeSessionMutationOrQueue(string text)
    {
        var store = new InMemoryEllaiSessionStore();
        var service = new EllaiUtteranceSubmissionService(store, new EllaiConsentService());
        var session = await StartSessionAsync(store, AuthenticatedPrincipal("actor-one"));
        var request = Request(session, "utterance-one") with { FinalizedText = text };

        var result = await service.SubmitAsync(
            session.SessionId, request, AuthenticatedPrincipal("actor-one"), CancellationToken.None);
        var after = await store.GetAsync(session.SessionId, CancellationToken.None);

        Assert.Equal(EllaiClientOperationStatus.PolicyBlocked, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(session.SessionVersion, after!.SessionVersion);
        Assert.Equal(0, after.CyclesUsed);
    }

    [Fact]
    public async Task ConsentForDifferentDataClassCannotAuthorizeUnknownOrSensitiveText()
    {
        var store = new InMemoryEllaiSessionStore();
        var consent = new EllaiConsentService();
        var service = new EllaiUtteranceSubmissionService(store, consent);
        var session = await StartSessionAsync(store, AuthenticatedPrincipal("actor-one"));
        consent.Grant(session.SessionId, EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo,
            TimeSpan.FromHours(1));

        var result = await service.SubmitAsync(
            session.SessionId, Request(session, "sensitive"),
            AuthenticatedPrincipal("actor-one"), CancellationToken.None);

        Assert.Equal(EllaiClientOperationStatus.PolicyBlocked, result.Status);
        Assert.False(consent.IsGranted(session.SessionId, EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.RealSensitive,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ExplicitPublicHelpConsentAcceptsOnlySanitizedCatalogPromptIntoOutbox()
    {
        var store = new InMemoryEllaiSessionStore();
        var consent = new EllaiConsentService();
        var service = new EllaiUtteranceSubmissionService(store, consent);
        var principal = AuthenticatedPrincipal("actor-one");
        var session = await StartSessionAsync(store, principal);
        consent.Grant(session.SessionId, EllaiConsentDimension.ReasoningText,
            EllaiConsentPurposes.PublicProductHelp,
            EllaiProcessingClassification.ConsentedNonSensitive, TimeSpan.FromHours(1));
        var originalText = "What does an intake receipt mean?";

        var result = await service.SubmitAsync(session.SessionId,
            Request(session, "safe-utterance") with { FinalizedText = originalText },
            principal, CancellationToken.None);

        Assert.Equal(EllaiClientOperationStatus.Accepted, result.Status);
        Assert.Equal(session.SessionVersion + 1, result.Session!.SessionVersion);
        var item = await store.ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        Assert.NotNull(item);
        var envelope = EllaiUtteranceWorkEnvelope.Deserialize(item!.PayloadJson);
        Assert.DoesNotContain(originalText, envelope.Utterance, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EllaiConsentPurposes.PublicProductHelp, envelope.ConsentPurpose);
        Assert.Equal([EllaiToolNames.ProductFacts], envelope.AllowedTools);
        Assert.Equal(EllaiProcessingClassification.ConsentedNonSensitive, envelope.DataClassification);
    }

    [Fact]
    public async Task ForeignActorAndUnauthenticatedPrincipalCannotSubmit()
    {
        var store = new InMemoryEllaiSessionStore();
        var service = new EllaiUtteranceSubmissionService(store, new EllaiConsentService());
        var session = await StartSessionAsync(store, AuthenticatedPrincipal("owner"));

        var foreign = await service.SubmitAsync(session.SessionId, Request(session, "foreign"),
            AuthenticatedPrincipal("other"), CancellationToken.None);
        var anonymous = await service.SubmitAsync(session.SessionId, Request(session, "anonymous"),
            new ClaimsPrincipal(new ClaimsIdentity()), CancellationToken.None);

        Assert.Equal(EllaiClientOperationStatus.Unauthorized, foreign.Status);
        Assert.Equal(EllaiClientOperationStatus.Unauthorized, anonymous.Status);
        Assert.Equal(session.SessionVersion,
            (await store.GetAsync(session.SessionId, CancellationToken.None))!.SessionVersion);
    }

    [Fact]
    public async Task StaleSubmissionReturnsCurrentOwnerViewWithoutConsumingCycle()
    {
        var store = new InMemoryEllaiSessionStore();
        var principal = AuthenticatedPrincipal("owner");
        var session = await StartSessionAsync(store, principal);
        var consent = new EllaiConsentService();
        consent.Grant(session.SessionId, EllaiConsentDimension.ReasoningText,
            EllaiConsentPurposes.PublicProductHelp,
            EllaiProcessingClassification.ConsentedNonSensitive, TimeSpan.FromHours(1));
        var service = new EllaiUtteranceSubmissionService(store, consent);
        var stale = await service.SubmitAsync(session.SessionId,
            Request(session, "stale") with
            {
                ExpectedSessionVersion = session.SessionVersion - 1,
                FinalizedText = "What does an intake receipt mean?",
            },
            principal, CancellationToken.None);

        Assert.Equal(EllaiClientOperationStatus.Conflict, stale.Status);
        Assert.Equal(session.SessionVersion, stale.Session!.SessionVersion);
        Assert.Equal(0, (await store.GetAsync(session.SessionId, CancellationToken.None))!.CyclesUsed);
    }

    [Fact]
    public void SafeSyntheticProcessingRequiresExplicitCurrentMatchingConsent()
    {
        var consent = new EllaiConsentService();
        Assert.True(EllaiPolicyService.IsContributorEligible(
            EllaiProcessingClassification.SyntheticDemo, EllaiProcessingClassification.SyntheticDemo));
        var grant = consent.Grant("session", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo,
            TimeSpan.FromHours(1));

        Assert.True(consent.IsGranted("session", grant.Dimension, grant.Purpose, grant.DataClass,
            DateTimeOffset.UtcNow));
        Assert.False(consent.IsGranted("session", grant.Dimension, grant.Purpose,
            EllaiProcessingClassification.RealSensitive, DateTimeOffset.UtcNow));
        Assert.False(EllaiPolicyService.IsContributorEligible(
            EllaiProcessingClassification.SyntheticDemo, "case-narrative"));
        Assert.False(EllaiPolicyService.IsContributorEligible(
            EllaiProcessingClassification.UnknownOrigin, EllaiProcessingClassification.SyntheticDemo));
        Assert.False(EllaiPolicyService.IsContributorEligible(
            EllaiProcessingClassification.RealSensitive, EllaiProcessingClassification.RealSensitive));
    }

    [Fact]
    public async Task StaleCancelCanRefreshOnlyTheCurrentOwnerView()
    {
        var store = new InMemoryEllaiSessionStore();
        var sessions = new EllaiSessionService(store);
        var owner = AuthenticatedPrincipal("owner");
        var other = AuthenticatedPrincipal("other");
        var session = await sessions.StartAsync(
            new StartSessionRequest(EllaiMode.Ask, "start", null, null, null, null),
            owner, CancellationToken.None);

        Assert.False(await sessions.CancelAsync(
            session.SessionId, session.SessionVersion + 1, owner, CancellationToken.None));
        var afterStale = await store.GetAsync(session.SessionId, CancellationToken.None);
        Assert.Equal(session.SessionVersion, afterStale!.SessionVersion);
        Assert.Equal("Active", afterStale.Status);
        Assert.False(await sessions.CancelAsync(
            session.SessionId, session.SessionVersion, other, CancellationToken.None));
        Assert.Equal(session.SessionVersion,
            (await store.GetAsync(session.SessionId, CancellationToken.None))!.SessionVersion);
        Assert.Null(await sessions.GetOwnedViewAsync(session.SessionId, other, CancellationToken.None));
        Assert.Null(await sessions.GetOwnedViewAsync(session.SessionId,
            new ClaimsPrincipal(new ClaimsIdentity()), CancellationToken.None));
    }

    private static Task<EllaiSessionView> StartSessionAsync(
        IEllaiSessionStore store, ClaimsPrincipal principal) =>
        new EllaiSessionService(store).StartAsync(
            new StartSessionRequest(EllaiMode.Ask, Guid.NewGuid().ToString("N"), null, null, null, null),
            principal, CancellationToken.None);

    private static SubmitUtteranceRequest Request(EllaiSessionView session, string id) => new(
        id, session.SessionId, session.SessionVersion, "What can I do next?", null, "typed", 1);

    private static ClaimsPrincipal AuthenticatedPrincipal(string actorId) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId),
            new Claim(ActorContext.AccessEpochClaimType, "1"),
        ], authenticationType: "test"));
}
