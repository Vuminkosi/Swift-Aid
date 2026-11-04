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
    [InlineData("typed", true, 0, true)]
    [InlineData("live-voice", true, 0, true)]
    [InlineData("recorded-voice", true, 0, true)]
    [InlineData("typed", true, 31, false)]
    [InlineData("typed", false, 0, false)]
    public async Task OpenCaseContextIsFreshAdvisoryAndSharedWithVoice(string mode, bool workspace, int ageSeconds, bool expected)
    {
        var store = new InMemoryEllaiSessionStore();
        var principal = AuthenticatedPrincipal("actor-one");
        var session = await StartSessionAsync(store, principal);
        if (workspace)
        {
            var current = await store.GetAsync(session.SessionId, default);
            await store.TryUpdateAsync(current! with { ScopeEpoch = "access-epoch-1" }, current.SessionVersion, default);
        }
        var observations = new InMemoryEllaiUiObservationStore();
        var caseId = Guid.NewGuid();
        await observations.RecordAsync(new(session.SessionId, "view", "stamp", "web", [],
            DateTimeOffset.UtcNow.AddSeconds(-ageSeconds), CurrentNavigationId: EllaiCaseDetailUiManifest.NavigationId,
            CurrentCaseId: caseId), default);
        var service = new EllaiUtteranceSubmissionService(store, new EllaiConsentService(), observations);
        var result = await service.SubmitAsync(session.SessionId, Request(session, "explain"), principal, default, mode);
        Assert.Equal(EllaiClientOperationStatus.Accepted, result.Status);
        var item = await store.ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default);
        var work = EllaiUtteranceWorkEnvelope.Deserialize(item!.PayloadJson).ToWork();
        Assert.Equal(expected ? caseId.ToString("D") : null, work.ResourceId);
        Assert.Equal(expected ? "case-details" : null, work.ActivePage);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("live-voice")]
    [InlineData("recorded-voice")]
    public async Task InteractionModeSurvivesDurableAdmissionAndRecovery(string mode)
    {
        var store = new InMemoryEllaiSessionStore();
        var service = new EllaiUtteranceSubmissionService(store, new EllaiConsentService());
        var principal = AuthenticatedPrincipal("actor-one");
        var session = await StartSessionAsync(store, principal);
        var result = await service.SubmitAsync(session.SessionId, Request(session, "mode-turn"),
            principal, CancellationToken.None, mode);
        Assert.Equal(EllaiClientOperationStatus.Accepted, result.Status);
        var item = await store.ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        var work = EllaiUtteranceWorkEnvelope.Deserialize(item!.PayloadJson).ToWork();
        Assert.Equal(mode, work.InteractionMode);
        Assert.Equal(mode, EllaiUtteranceWorkEnvelope.Deserialize(
            EllaiUtteranceWorkEnvelope.Serialize(work, work.ClientUtteranceId)).ToWork().InteractionMode);
        Assert.DoesNotContain(EllaiToolNames.State, work.AllowedTools!);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("show my assigned cases")]
    [InlineData("unclassifiable arbitrary text")]
    public async Task FreeTextIsAcceptedForDynamicReasoningWithinPublicToolScope(string text)
    {
        var store = new InMemoryEllaiSessionStore();
        var consent = new EllaiConsentService();
        var service = new EllaiUtteranceSubmissionService(store, consent);
        var session = await StartSessionAsync(store, AuthenticatedPrincipal("actor-one"));
        var request = Request(session, "utterance-one") with { FinalizedText = text };

        var result = await service.SubmitAsync(
            session.SessionId, request, AuthenticatedPrincipal("actor-one"), CancellationToken.None);
        var after = await store.GetAsync(session.SessionId, CancellationToken.None);

        Assert.Equal(EllaiClientOperationStatus.Accepted, result.Status);
        Assert.Equal(session.SessionVersion + 1, result.Session!.SessionVersion);
        Assert.Equal(session.SessionVersion + 1, after!.SessionVersion);
        Assert.Equal(1, after.CyclesUsed);
        var item = await store.ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        Assert.NotNull(item);
        var envelope = EllaiUtteranceWorkEnvelope.Deserialize(item!.PayloadJson);
        Assert.Equal(text, envelope.Utterance);
        Assert.Equal(new[] { EllaiToolNames.ProductFacts, EllaiToolNames.IngestionSchema, EllaiToolNames.ValidateObservations, EllaiToolNames.Locations, EllaiToolNames.UiCatalogue, EllaiToolNames.UiObserve, EllaiToolNames.PublicSessions, EllaiToolNames.Now, EllaiToolNames.Capabilities, EllaiToolNames.Receipts }.Order(), envelope.AllowedTools!.Order());
    }

    [Fact]
    public async Task TypedSubmissionDoesNotCreateOrRequireAReasoningConsentGrant()
    {
        var store = new InMemoryEllaiSessionStore();
        var consent = new EllaiConsentService();
        var service = new EllaiUtteranceSubmissionService(store, consent);
        var session = await StartSessionAsync(store, AuthenticatedPrincipal("actor-one"));
        var result = await service.SubmitAsync(
            session.SessionId, Request(session, "sensitive"),
            AuthenticatedPrincipal("actor-one"), CancellationToken.None);

        Assert.Equal(EllaiClientOperationStatus.Accepted, result.Status);
        Assert.False(consent.IsGranted(session.SessionId, EllaiConsentDimension.ReasoningText,
            EllaiConsentPurposes.PublicProductHelp,
            EllaiProcessingClassification.UserProvidedNonSensitive, DateTimeOffset.UtcNow));
        var item = await store.ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        Assert.NotNull(item);
        var work = EllaiUtteranceWorkEnvelope.Deserialize(item!.PayloadJson);
        Assert.Equal("consent/0", work.ConsentVersion);
        Assert.Equal(EllaiProcessingClassification.UserProvidedNonSensitive, work.ProcessingLabel);
        Assert.True(EllaiResponsePublicationPolicy.CanPublish(work.ToWork()));
    }

    [Fact]
    public async Task WorkspaceScopedSessionCanExposeAuthorizedReadToolsToDynamicReasoning()
    {
        var store = new InMemoryEllaiSessionStore();
        var consent = new EllaiConsentService();
        var service = new EllaiUtteranceSubmissionService(store, consent);
        var principal = AuthenticatedPrincipal("actor-one");
        var session = await StartSessionAsync(store, principal);
        var current = await store.GetAsync(session.SessionId, CancellationToken.None);
        await store.TryUpdateAsync(current! with { ScopeEpoch = "access-epoch-1" },
            current!.SessionVersion, CancellationToken.None);

        var result = await service.SubmitAsync(session.SessionId,
            Request(session, "assigned-cases") with { FinalizedText = "show my assigned cases" },
            principal, CancellationToken.None);

        Assert.Equal(EllaiClientOperationStatus.Accepted, result.Status);
        var item = await store.ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        Assert.NotNull(item);
        var envelope = EllaiUtteranceWorkEnvelope.Deserialize(item!.PayloadJson);
        Assert.Contains(EllaiToolNames.State, envelope.AllowedTools);
    }

    [Theory]
    [InlineData("access-epoch-1")]
    [InlineData("ellai-help-identity-epoch-1")]
    public async Task ChangedAuthenticatedEpochCannotQueueATurn(string scopeEpoch)
    {
        var store = new InMemoryEllaiSessionStore();
        var service = new EllaiUtteranceSubmissionService(store, new EllaiConsentService());
        var original = AuthenticatedPrincipal("actor-one");
        var session = await StartSessionAsync(store, original);
        var current = (await store.GetAsync(session.SessionId, default))!;
        await store.TryUpdateAsync(current with { ScopeEpoch = scopeEpoch }, current.SessionVersion, default);
        var newEpoch = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "actor-one"),
            new Claim(ActorContext.AccessEpochClaimType, "2")], "test"));

        var result = await service.SubmitAsync(session.SessionId, Request(session, "changed-epoch"), newEpoch, default);

        Assert.Equal(EllaiClientOperationStatus.PolicyBlocked, result.Status);
        Assert.Null(result.Session);
        Assert.Null(await store.ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default));
        Assert.Equal(session.SessionVersion, (await store.GetAsync(session.SessionId, default))!.SessionVersion);
    }

    [Fact]
    public async Task GuestScopeKeepsDynamicIntentWithinPublicCaptureAssistance()
    {
        var store = new InMemoryEllaiSessionStore();
        var service = new EllaiUtteranceSubmissionService(store, new EllaiConsentService());
        var principal = AuthenticatedPrincipal("guest-one");
        var session = await StartSessionAsync(store, principal);
        var current = await store.GetAsync(session.SessionId, CancellationToken.None);
        await store.TryUpdateAsync(current! with { ScopeEpoch = "ellai-help-identity-epoch-1" },
            current!.SessionVersion, CancellationToken.None);

        var result = await service.SubmitAsync(session.SessionId,
            Request(session, "guest-assigned-cases") with { FinalizedText = "show assigned cases" },
            principal, CancellationToken.None);

        Assert.Equal(EllaiClientOperationStatus.Accepted, result.Status);
        var item = await store.ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        Assert.NotNull(item);
        var envelope = EllaiUtteranceWorkEnvelope.Deserialize(item!.PayloadJson);
        Assert.Equal(new[] { EllaiToolNames.ProductFacts, EllaiToolNames.IngestionSchema, EllaiToolNames.ValidateObservations, EllaiToolNames.Locations, EllaiToolNames.UiCatalogue, EllaiToolNames.UiObserve, EllaiToolNames.PublicSessions, EllaiToolNames.Now, EllaiToolNames.Capabilities, EllaiToolNames.Receipts }.Order(), envelope.AllowedTools!.Order());
    }

    [Fact]
    public async Task ExplicitPublicHelpConsentDoesNotChangeOrdinaryTypedSubmission()
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
        Assert.Equal(originalText, envelope.Utterance);
        Assert.Equal(EllaiConsentPurposes.PublicProductHelp, envelope.ConsentPurpose);
        Assert.Equal(new[] { EllaiToolNames.ProductFacts, EllaiToolNames.IngestionSchema, EllaiToolNames.ValidateObservations, EllaiToolNames.Locations, EllaiToolNames.UiCatalogue, EllaiToolNames.UiObserve, EllaiToolNames.PublicSessions, EllaiToolNames.Now, EllaiToolNames.Capabilities, EllaiToolNames.Receipts }.Order(), envelope.AllowedTools!.Order());
        Assert.Equal(EllaiProcessingClassification.UserProvidedNonSensitive, envelope.DataClassification);
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
