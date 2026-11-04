using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Cases;
using SwiftAid.Domain.Intake;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;
using SwiftAid.Infrastructure.Persistence.Repositories.Intake;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Opt-in PostgreSQL tests proving original-audio reservations transition from
/// <c>Uploaded</c> to <c>Attached</c> inside the same atomic intake commit, stay
/// attached across idempotent retries, and can never back a second report.
/// Uses the same disposable loopback database gate as
/// <see cref="IntakePostgresFactAttribute"/>.
/// </summary>
[Trait("Category", "IntakePostgres")]
[Collection(IntakePostgresCollection.Name)]
public sealed class IntakeSubmissionAttachmentBindingTests
{
    [IntakePostgresFact]
    public async Task Submit_WithBoundReservation_AttachesInSameTransaction()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var fixture = await SeedBoundReservationAsync(db);
        var service = CreateService(db);

        var command = CreateSubmission(fixture.ClientSubmissionId, [fixture.AttachmentId]);
        var receipt = await service.SubmitAsync(command, fixture.Capability, CancellationToken.None);

        Assert.Equal(fixture.ClientSubmissionId, receipt.ClientSubmissionId);
        var row = await db.IntakeAttachments.AsNoTracking()
            .SingleAsync(item => item.AttachmentId == fixture.AttachmentId);
        Assert.Equal(IntakeAttachmentState.Attached, row.State);
        Assert.Equal(TruncateToMicroseconds(receipt.ReceivedAtUtc), row.AttachedAtUtc);
        Assert.Equal(1, await db.IntakeReports.CountAsync(
            item => item.ClientSubmissionId == fixture.ClientSubmissionId));
    }

    [IntakePostgresFact]
    public async Task Replay_SubmitTwice_KeepsSingleReportAndAttachment()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var fixture = await SeedBoundReservationAsync(db);
        var service = CreateService(db);

        var command = CreateSubmission(fixture.ClientSubmissionId, [fixture.AttachmentId]);
        var first = await service.SubmitAsync(command, fixture.Capability, CancellationToken.None);
        var second = await service.SubmitAsync(command, fixture.Capability, CancellationToken.None);

        Assert.Equal(first.ReceiptId, second.ReceiptId);
        Assert.Equal(1, await db.IntakeReports.CountAsync(
            item => item.ClientSubmissionId == fixture.ClientSubmissionId));
        var row = await db.IntakeAttachments.AsNoTracking()
            .SingleAsync(item => item.AttachmentId == fixture.AttachmentId);
        Assert.Equal(IntakeAttachmentState.Attached, row.State);
        Assert.Equal(TruncateToMicroseconds(first.ReceivedAtUtc), row.AttachedAtUtc);
    }

    [IntakePostgresFact]
    public async Task Submit_WithForeignReservationBinding_RejectsWithoutReport()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var fixture = await SeedBoundReservationAsync(db);
        var service = CreateService(db);

        // The reservation is bound to the seeded submission id; presenting it under a
        // different client submission must fail closed with no report persisted.
        var foreignSubmissionId = Guid.NewGuid();
        var command = CreateSubmission(foreignSubmissionId, [fixture.AttachmentId]);
        var failure = await Assert.ThrowsAsync<IntakeSubmissionException>(
            () => service.SubmitAsync(command, fixture.Capability, CancellationToken.None));
        Assert.Equal(IntakeErrorCode.CapabilityInvalid, failure.Code);

        Assert.Equal(0, await db.IntakeReports.CountAsync(
            item => item.ClientSubmissionId == foreignSubmissionId));
        var row = await db.IntakeAttachments.AsNoTracking()
            .SingleAsync(item => item.AttachmentId == fixture.AttachmentId);
        Assert.Equal(IntakeAttachmentState.Uploaded, row.State);
        Assert.Null(row.AttachedAtUtc);
    }

    private static IntakeService CreateService(SwiftAidDbContext db)
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        using var provider = services.BuildServiceProvider();
        var dataProtection = provider.GetRequiredService<IDataProtectionProvider>();
        return new IntakeService(
            new PostgresIntakeSubmissionStore(db, IntakeLimits.Default, dataProtection),
            new PostgresIntakeDecisionStore(db),
            new UnusedPromotionService(),
            new PostgresIntakeAttachmentAuthority(db),
            new CoverageRouter(new NoCoverageCatalog()),
            new AcceptingSubmissionValidator(),
            IntakeLimits.Default,
            TimeProvider.System);
    }

    private static IntakeSubmission CreateSubmission(Guid clientSubmissionId, Guid[] attachmentIds)
    {
        var now = DateTimeOffset.UtcNow;
        return new IntakeSubmission(
            1,
            clientSubmissionId,
            IntakeSubjectScope.Human,
            "A concern was reported.",
            ["Fever since morning."],
            attachmentIds,
            new ReportedTime(now, null, null, null, "+00:00", null, ReportedTimePrecision.Approximate),
            new ReportedTime(now.AddHours(-2), null, null, null, "+02:00", null, ReportedTimePrecision.Approximate),
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
    }

    private sealed record BoundFixture(IntakeCapability Capability, Guid ClientSubmissionId, Guid AttachmentId);

    /// <summary>PostgreSQL timestamptz stores whole microseconds; .NET ticks carry 100ns.</summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % 10));

    private static async Task<BoundFixture> SeedBoundReservationAsync(SwiftAidDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid();
        var capabilityId = Guid.NewGuid();
        var clientSubmissionId = Guid.NewGuid();
        var capabilityDigest = RandomNumberGenerator.GetBytes(32);
        db.IntakeSessions.Add(new IntakeSessionEntity
        {
            SessionId = sessionId,
            ReceiptProofDigest = capabilityDigest,
            CreatedAtUtc = now,
            LastSeenAtUtc = now
        });
        db.IntakeCapabilities.Add(new IntakeCapabilityEntity
        {
            CapabilityId = capabilityId,
            SessionId = sessionId,
            CapabilityDigest = capabilityDigest,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(1)
        });
        var attachmentId = Guid.NewGuid();
        db.IntakeAttachments.Add(new IntakeAttachmentEntity
        {
            AttachmentId = attachmentId,
            SessionId = sessionId,
            CapabilityId = capabilityId,
            ClientSubmissionId = clientSubmissionId,
            ObjectKey = $"intake-test/{Guid.NewGuid():N}.ogg",
            ContentType = "audio/ogg",
            SizeBytes = 2048,
            DurationSeconds = 45,
            State = IntakeAttachmentState.Uploaded,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(1)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var capability = new IntakeCapability(
            capabilityId, sessionId, capabilityDigest, now.AddHours(1), 10);
        return new BoundFixture(capability, clientSubmissionId, attachmentId);
    }

    private sealed class NoCoverageCatalog : IIntakeCoverageCatalog
    {
        public Task<IntakeCoverageDecision> FindAsync(LocationContext location, CaseKind kind, CancellationToken ct) =>
            Task.FromResult(new IntakeCoverageDecision(IntakeCoverageMatch.None, null));
    }

    private sealed class AcceptingSubmissionValidator : IIntakeSubmissionValidator
    {
        public Task<bool> ValidateAsync(IntakeSubmission submission, IntakeLimits limits, CancellationToken ct) =>
            Task.FromResult(true);
    }

    private sealed class UnusedPromotionService : IIntakePromotionService
    {
        public Task<IntakeDecisionResult> PromoteAsync(
            IntakeDecision command, ActorContext actor, CancellationToken ct) =>
            throw new NotSupportedException("Submission tests never promote.");
    }
}
