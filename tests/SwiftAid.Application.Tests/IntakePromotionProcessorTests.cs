using System.Collections.Immutable;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Cases;
using SwiftAid.Domain.Intake;

namespace SwiftAid.Application.Tests;

public sealed class IntakePromotionProcessorTests
{
    [Fact]
    public async Task Promote_HappyPath_AppliesWithCase()
    {
        var receiptId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var receipt = new IntakeReceipt(receiptId, Guid.NewGuid(), IntakeStatus.AcceptedIntoCase, IntakeRoutingStatus.RoutedForReview, DateTimeOffset.UtcNow);
        var harness = new Harness(
            source: Source(receiptId, version: 1),
            submission: HumanSubmission(),
            workspaces: ["workspace-1"],
            registration: new CaseRegistrationResult(
                CaseRegistrationStatus.Accepted, caseId, IntakePromotionIds.CommandId(receiptId),
                ImmutableArray<Guid>.Empty, 2, 1),
            apply: new IntakeDecisionResult(IntakeDecisionStatus.Applied, receipt, caseId));

        var result = await harness.Processor.PromoteAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.AcceptIntoCase, "Looks actionable."),
            CreateOfficer(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Applied, result.Status);
        Assert.Equal(receipt, result.Receipt);
        Assert.Equal(caseId, result.PromotedCaseId);
        Assert.NotNull(harness.RegisteredRequest);
        Assert.Equal(IntakePromotionIds.CommandId(receiptId), harness.RegisteredRequest.CommandId);
        Assert.Equal("workspace-1", harness.RegisteredRequest.WorkspaceId);
    }

    [Fact]
    public async Task Promote_ReplayedRegistration_StillApplies()
    {
        var receiptId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var harness = new Harness(
            source: Source(receiptId, version: 1),
            submission: HumanSubmission(),
            workspaces: ["workspace-1"],
            registration: new CaseRegistrationResult(
                CaseRegistrationStatus.Replayed, caseId, IntakePromotionIds.CommandId(receiptId),
                ImmutableArray<Guid>.Empty, 2, 1),
            apply: new IntakeDecisionResult(
                IntakeDecisionStatus.Applied,
                new IntakeReceipt(receiptId, Guid.NewGuid(), IntakeStatus.AcceptedIntoCase, IntakeRoutingStatus.RoutedForReview, DateTimeOffset.UtcNow),
                caseId));

        var result = await harness.Processor.PromoteAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.AcceptIntoCase, null),
            CreateOfficer(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Applied, result.Status);
        Assert.Equal(caseId, result.PromotedCaseId);
    }

    [Fact]
    public async Task Promote_TerminalReplay_ResolvesSameCaseWithoutRegistering()
    {
        var receiptId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var harness = new Harness(
            source: Source(receiptId, version: 2, IntakeStatus.AcceptedIntoCase),
            submission: HumanSubmission(),
            workspaces: ["workspace-1"],
            promoted: caseId);

        var result = await harness.Processor.PromoteAsync(
            new IntakeDecision(receiptId, 2, IntakeDecisionKind.AcceptIntoCase, null),
            CreateOfficer(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Applied, result.Status);
        Assert.Equal(caseId, result.PromotedCaseId);
        Assert.Equal(IntakeStatus.AcceptedIntoCase, result.Receipt!.Status);
        Assert.Null(harness.RegisteredRequest);
    }

    [Fact]
    public async Task Promote_TerminalReplayWithoutLink_Conflicts()
    {
        var receiptId = Guid.NewGuid();
        var harness = new Harness(
            source: Source(receiptId, version: 2, IntakeStatus.AcceptedIntoCase),
            submission: HumanSubmission(),
            workspaces: ["workspace-1"],
            promoted: null);

        var result = await harness.Processor.PromoteAsync(
            new IntakeDecision(receiptId, 2, IntakeDecisionKind.AcceptIntoCase, null),
            CreateOfficer(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Conflict, result.Status);
        Assert.Null(harness.RegisteredRequest);
    }

    [Fact]
    public async Task Promote_UnknownReceipt_ReturnsNotFound()
    {
        var harness = new Harness(
            source: null,
            submission: HumanSubmission(),
            workspaces: ["workspace-1"]);

        var result = await harness.Processor.PromoteAsync(
            new IntakeDecision(Guid.NewGuid(), 1, IntakeDecisionKind.AcceptIntoCase, null),
            CreateOfficer(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.NotFoundOrNotAuthorized, result.Status);
    }

    [Fact]
    public async Task Promote_UnroutedReport_IsInvalid()
    {
        var receiptId = Guid.NewGuid();
        var harness = new Harness(
            source: Source(receiptId, version: 1, routing: IntakeRoutingStatus.NeedsRouting, queueId: null),
            submission: HumanSubmission(),
            workspaces: ["workspace-1"]);

        var result = await harness.Processor.PromoteAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.AcceptIntoCase, null),
            CreateOfficer(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Invalid, result.Status);
        Assert.Null(harness.RegisteredRequest);
    }

    [Fact]
    public async Task Promote_ReviewerNamedTarget_IsInvalid()
    {
        var harness = new Harness(
            source: Source(Guid.NewGuid(), version: 1),
            submission: HumanSubmission(),
            workspaces: ["workspace-1"]);

        var result = await harness.Processor.PromoteAsync(
            new IntakeDecision(Guid.NewGuid(), 1, IntakeDecisionKind.AcceptIntoCase, null, Guid.NewGuid()),
            CreateOfficer(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Promote_NoEligibleWorkspace_ReturnsNotFound()
    {
        var receiptId = Guid.NewGuid();
        var harness = new Harness(
            source: Source(receiptId, version: 1),
            submission: HumanSubmission(),
            workspaces: []);

        var result = await harness.Processor.PromoteAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.AcceptIntoCase, null),
            CreateOfficer(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.NotFoundOrNotAuthorized, result.Status);
        Assert.Null(harness.RegisteredRequest);
    }

    [Fact]
    public async Task Promote_AmbiguousWorkspaces_Conflicts()
    {
        var receiptId = Guid.NewGuid();
        var harness = new Harness(
            source: Source(receiptId, version: 1),
            submission: HumanSubmission(),
            workspaces: ["workspace-1", "workspace-2"]);

        var result = await harness.Processor.PromoteAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.AcceptIntoCase, null),
            CreateOfficer(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Conflict, result.Status);
        Assert.Null(harness.RegisteredRequest);
    }

    [Fact]
    public async Task Promote_RegistrationDenied_ReturnsNotFound()
    {
        var receiptId = Guid.NewGuid();
        var harness = new Harness(
            source: Source(receiptId, version: 1),
            submission: HumanSubmission(),
            workspaces: ["workspace-1"],
            registration: CaseRegistrationResult.WithoutCase(
                CaseRegistrationStatus.Denied, IntakePromotionIds.CommandId(receiptId)));

        var result = await harness.Processor.PromoteAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.AcceptIntoCase, null),
            CreateOfficer(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.NotFoundOrNotAuthorized, result.Status);
    }

    [Fact]
    public async Task Promote_RegistrationUnavailable_ThrowsPromotionUnavailable()
    {
        var receiptId = Guid.NewGuid();
        var harness = new Harness(
            source: Source(receiptId, version: 1),
            submission: HumanSubmission(),
            workspaces: ["workspace-1"],
            registration: CaseRegistrationResult.WithoutCase(
                CaseRegistrationStatus.Unavailable, IntakePromotionIds.CommandId(receiptId)));

        await Assert.ThrowsAsync<IntakePromotionUnavailableException>(() =>
            harness.Processor.PromoteAsync(
                new IntakeDecision(receiptId, 1, IntakeDecisionKind.AcceptIntoCase, null),
                CreateOfficer(),
                CancellationToken.None));
    }

    [Fact]
    public async Task Promote_RegistrationThrows_ThrowsPromotionUnavailable()
    {
        var receiptId = Guid.NewGuid();
        var harness = new Harness(
            source: Source(receiptId, version: 1),
            submission: HumanSubmission(),
            workspaces: ["workspace-1"],
            registrationThrows: new InvalidOperationException("database is down"));

        await Assert.ThrowsAsync<IntakePromotionUnavailableException>(() =>
            harness.Processor.PromoteAsync(
                new IntakeDecision(receiptId, 1, IntakeDecisionKind.AcceptIntoCase, null),
                CreateOfficer(),
                CancellationToken.None));
    }

    [Fact]
    public async Task Promote_ApplyConflict_ConvergesOnWinnerCase()
    {
        var receiptId = Guid.NewGuid();
        var winnerCaseId = Guid.NewGuid();
        var registeredCaseId = Guid.NewGuid();
        var harness = new Harness(
            source: Source(receiptId, version: 1),
            submission: HumanSubmission(),
            workspaces: ["workspace-1"],
            registration: new CaseRegistrationResult(
                CaseRegistrationStatus.Accepted, registeredCaseId, IntakePromotionIds.CommandId(receiptId),
                ImmutableArray<Guid>.Empty, 2, 1),
            apply: new IntakeDecisionResult(IntakeDecisionStatus.Conflict, null),
            promoted: winnerCaseId);

        var result = await harness.Processor.PromoteAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.AcceptIntoCase, null),
            CreateOfficer(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Applied, result.Status);
        Assert.Equal(winnerCaseId, result.PromotedCaseId);
    }

    [Fact]
    public async Task Promote_ApplyConflictWithoutLink_Conflicts()
    {
        var receiptId = Guid.NewGuid();
        var registeredCaseId = Guid.NewGuid();
        var harness = new Harness(
            source: Source(receiptId, version: 1),
            submission: HumanSubmission(),
            workspaces: ["workspace-1"],
            registration: new CaseRegistrationResult(
                CaseRegistrationStatus.Accepted, registeredCaseId, IntakePromotionIds.CommandId(receiptId),
                ImmutableArray<Guid>.Empty, 2, 1),
            apply: new IntakeDecisionResult(IntakeDecisionStatus.Conflict, null),
            promoted: null);

        var result = await harness.Processor.PromoteAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.AcceptIntoCase, null),
            CreateOfficer(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Conflict, result.Status);
    }

    [Fact]
    public async Task Promote_EmptySubmission_IsInvalid()
    {
        var receiptId = Guid.NewGuid();
        var harness = new Harness(
            source: Source(receiptId, version: 1),
            submission: HumanSubmission(narrative: "  ", observations: []),
            workspaces: ["workspace-1"]);

        var result = await harness.Processor.PromoteAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.AcceptIntoCase, null),
            CreateOfficer(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Invalid, result.Status);
        Assert.Null(harness.RegisteredRequest);
    }

    [Fact]
    public void ToReport_Human_PreservesEvidenceAndMarksClinicalFactsUnknown()
    {
        var receiptId = Guid.NewGuid();

        var report = IntakePromotionMapper.ToReport(receiptId, HumanSubmission());

        var human = Assert.IsType<CaseReportEventData.HumanV2>(report);
        Assert.False(human.Report.PseudonymousSubjectReference.Value.HasValue);
        Assert.False(human.Report.AgeYears.Value.HasValue);
        Assert.False(human.Report.ReportedConcern.Value.HasValue);
        Assert.Equal("A concern was reported.", human.Report.Evidence.Narrative!.Text);
        Assert.Equal(
            ["Fever since morning.", "No travel history."],
            human.Report.Evidence.Observations.Select(item => item.Text).ToArray());
        Assert.Equal("ZW", human.Confirmations.OccurrenceCountryCode);
    }

    [Fact]
    public void ReportedHumanAgeIsValidatedAndPromotedWithReporterProvenance()
    {
        var receiptId = Guid.NewGuid();
        var submission = HumanSubmission() with { AgeYears = 26 };
        IntakeSubmissionRules.Validate(submission);

        var human = Assert.IsType<CaseReportEventData.HumanV2>(IntakePromotionMapper.ToReport(receiptId, submission));
        Assert.True(human.Report.AgeYears.Value.HasValue);
        Assert.Equal(26, human.Report.AgeYears.Value.Value);
        Assert.Equal(ReportSourceKind.ReporterObservation, human.Report.AgeYears.Provenance.Source);
        Assert.Equal($"intake:{receiptId:N}:age", human.Report.AgeYears.Provenance.StableReference);
        Assert.Throws<ArgumentException>(() => IntakeSubmissionRules.Validate(submission with { AgeYears = -1 }));
        Assert.Throws<ArgumentException>(() => IntakeSubmissionRules.Validate(submission with { SubjectScope = IntakeSubjectScope.Herd }));
        Assert.Throws<ArgumentException>(() => IntakePromotionMapper.ToReport(receiptId, submission with { SubjectScope = IntakeSubjectScope.Herd }));
    }

    [Fact]
    public void UnknownAgeKeepsLegacySerializedPayloadAndKnownAgeRoundTrips()
    {
        var submission = HumanSubmission();
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var json = System.Text.Json.JsonSerializer.Serialize(submission, options);
        Assert.DoesNotContain("ageYears", json);
        var known = System.Text.Json.JsonSerializer.Serialize(submission with { AgeYears = 0 }, options);
        Assert.Equal(0, System.Text.Json.JsonSerializer.Deserialize<IntakeSubmission>(known, options)!.AgeYears);
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<IntakeSubmission>(json, options)!.AgeYears);
    }

    [Fact]
    public void ToReport_Animal_MapsSubjectScopeAndLeavesCountsUnknown()
    {
        var baseSubmission = HumanSubmission();

        var report = IntakePromotionMapper.ToReport(
            Guid.NewGuid(), baseSubmission with { SubjectScope = IntakeSubjectScope.Herd });

        var animal = Assert.IsType<CaseReportEventData.AnimalV2>(report);
        Assert.Equal(AnimalSubjectScope.Herd, animal.Report.SubjectScope);
        Assert.False(animal.Report.AnimalsAtRisk.Value.HasValue);
    }

    [Fact]
    public void ToReport_AudioFirst_LinkedByStableIntakeReference()
    {
        var audioId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        var baseSubmission = HumanSubmission(audio: [audioId, Guid.NewGuid()]);

        var report = IntakePromotionMapper.ToReport(
            receiptId, baseSubmission with
            {
                Confirmations = new IntakeConfirmations(
                    true, "ZW", "catalogue-1", true, true, "policy-1", "v1", true, "policy-1", "v1")
            });

        var human = Assert.IsType<CaseReportEventData.HumanV2>(report);
        Assert.Equal(
            $"intake-attachment:{audioId:D}",
            human.Report.Evidence.OriginalAudio!.Reference);
    }

    [Fact]
    public void PromotionIds_AreDeterministicAndDistinct()
    {
        var receiptId = Guid.NewGuid();

        Assert.Equal(IntakePromotionIds.CommandId(receiptId), IntakePromotionIds.CommandId(receiptId));
        Assert.Equal(IntakePromotionIds.ReportId(receiptId), IntakePromotionIds.ReportId(receiptId));
        Assert.NotEqual(IntakePromotionIds.CommandId(receiptId), IntakePromotionIds.ReportId(receiptId));
        Assert.NotEqual(IntakePromotionIds.CommandId(receiptId), IntakePromotionIds.CommandId(Guid.NewGuid()));
    }

    private static ActorContext CreateOfficer() => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "officer-1"),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "synthetic-test")));

    private static IntakePromotionSource Source(
        Guid receiptId,
        long version,
        IntakeStatus status = IntakeStatus.AwaitingReview,
        IntakeRoutingStatus routing = IntakeRoutingStatus.RoutedForReview,
        string? queueId = "queue-zw-harare") =>
        new(receiptId, Guid.NewGuid(), status, version, routing, queueId, "org-zw-1", [], DateTimeOffset.UtcNow);

    private static IntakeSubmission HumanSubmission(
        string? narrative = "A concern was reported.",
        string[]? observations = null,
        Guid[]? audio = null) =>
        new(
            1,
            Guid.NewGuid(),
            IntakeSubjectScope.Human,
            narrative,
            observations ?? ["Fever since morning.", "No travel history."],
            audio ?? [],
            new ReportedTime(DateTimeOffset.UtcNow, null, null, null, "+00:00", null, ReportedTimePrecision.Approximate),
            new ReportedTime(DateTimeOffset.UtcNow.AddHours(-3), null, null, null, "+02:00", null, ReportedTimePrecision.Approximate),
            new LocationContext(
                "ZW",
                new OptionalPlace(null, null, PresenceState.Unknown),
                new OptionalPlace(null, null, PresenceState.Unknown),
                new OptionalPlace(null, null, PresenceState.Unknown),
                null,
                null,
                null,
                LocationSource.Reporter,
                LocationPrecision.Approximate,
                "catalogue-1"),
            new IntakeConfirmations(true, "ZW", "catalogue-1", true, true, "policy-1", "v1", null, null, null));

    private sealed class Harness
    {
        private RegisterManagedCaseRequest? _registered;

        public Harness(
            IntakePromotionSource? source,
            IntakeSubmission submission,
            IReadOnlyList<string> workspaces,
            CaseRegistrationResult? registration = null,
            IntakeDecisionResult? apply = null,
            Guid? promoted = null,
            Exception? registrationThrows = null)
        {
            Processor = new IntakePromotionProcessor(
                new StubSource(source, promoted),
                new StubOpener(submission),
                new StubWorkspaces(workspaces),
                new StubRegistration(registration, registrationThrows, request => _registered = request),
                new StubPromotions(apply ?? new IntakeDecisionResult(IntakeDecisionStatus.Applied, null)),
                TimeProvider.System);
        }

        public IntakePromotionProcessor Processor { get; }

        public RegisterManagedCaseRequest? RegisteredRequest => _registered;
    }

    private sealed class StubSource(IntakePromotionSource? source, Guid? promoted) : IIntakePromotionSource
    {
        public Task<IntakePromotionSource?> ReadSourceAsync(Guid receiptId, CancellationToken ct) =>
            Task.FromResult(source);

        public Task<Guid?> FindPromotedCaseIdAsync(Guid receiptId, CancellationToken ct) =>
            Task.FromResult(promoted);
    }

    private sealed class StubOpener(IntakeSubmission submission) : IIntakeSubmissionOpener
    {
        public Task<IntakeSubmission> OpenAsync(byte[] protectedPayload, CancellationToken ct) =>
            Task.FromResult(submission);
    }

    private sealed class StubWorkspaces(IReadOnlyList<string> workspaces) : IIntakePromotionWorkspaceResolver
    {
        public Task<IReadOnlyList<string>> FindEligibleWorkspacesAsync(
            ActorContext actor, string organizationId, string countryCode, Sector sector, CancellationToken ct) =>
            Task.FromResult(workspaces);
    }

    private sealed class StubRegistration(
        CaseRegistrationResult? result,
        Exception? throws,
        Action<RegisterManagedCaseRequest> capture) : IManagedCaseRegistrationProcessor
    {
        public ValueTask<CaseRegistrationResult> RegisterAsync(
            ActorContext actor, RegisterManagedCaseRequest request, CancellationToken cancellationToken)
        {
            capture(request);
            if (throws is not null)
            {
                throw throws;
            }

            return ValueTask.FromResult(result ?? throw new InvalidOperationException("No stubbed registration."));
        }
    }

    private sealed class StubPromotions(IntakeDecisionResult result) : IIntakeDecisionStore
    {
        public Task<IntakeDecisionResult> DecideAsync(IntakeDecision decision, ActorContext actor, DateTimeOffset nowUtc, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IntakeDecisionResult> ApplyPromotionAsync(IntakePromotionApplication application, DateTimeOffset nowUtc, CancellationToken ct) =>
            Task.FromResult(result);
    }
}
