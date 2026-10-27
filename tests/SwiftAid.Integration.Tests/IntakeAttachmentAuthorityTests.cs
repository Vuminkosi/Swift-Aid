using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;
using SwiftAid.Infrastructure.Persistence.Repositories.Intake;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Opt-in PostgreSQL tests for the intake attachment authority. Uses the same disposable
/// loopback <c>swiftaid_intake_test</c> database gate as <see cref="IntakePostgresFactAttribute"/>.
/// </summary>
[Trait("Category", "IntakePostgres")]
public sealed class IntakeAttachmentAuthorityTests
{
    private const int MaxDurationSeconds = 120;
    private const long MaxBytesPerAttachment = 10 * 1024 * 1024;

    [IntakePostgresFact]
    public async Task BoundUpload_IsValid()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var fixture = await SeedBindingAsync(db);
        var authority = new PostgresIntakeAttachmentAuthority(db);

        var status = await authority.ValidateSubmissionAttachmentsAsync(
            fixture.Capability, fixture.ClientSubmissionId, [fixture.AttachmentId],
            MaxDurationSeconds, MaxBytesPerAttachment, CancellationToken.None);

        Assert.Equal(IntakeAttachmentValidationStatus.Valid, status);
    }

    [IntakePostgresFact]
    public async Task UnknownAttachmentId_IsInvalidBinding()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var fixture = await SeedBindingAsync(db);
        var authority = new PostgresIntakeAttachmentAuthority(db);

        var status = await authority.ValidateSubmissionAttachmentsAsync(
            fixture.Capability, fixture.ClientSubmissionId, [Guid.NewGuid()],
            MaxDurationSeconds, MaxBytesPerAttachment, CancellationToken.None);

        Assert.Equal(IntakeAttachmentValidationStatus.InvalidBinding, status);
    }

    [IntakePostgresFact]
    public async Task ForeignCapability_IsInvalidBinding()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var fixture = await SeedBindingAsync(db);
        var authority = new PostgresIntakeAttachmentAuthority(db);
        var foreign = fixture.Capability with { CapabilityId = Guid.NewGuid() };

        var status = await authority.ValidateSubmissionAttachmentsAsync(
            foreign, fixture.ClientSubmissionId, [fixture.AttachmentId],
            MaxDurationSeconds, MaxBytesPerAttachment, CancellationToken.None);

        Assert.Equal(IntakeAttachmentValidationStatus.InvalidBinding, status);
    }

    [IntakePostgresFact]
    public async Task ForeignSession_IsInvalidBinding()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var fixture = await SeedBindingAsync(db);
        var authority = new PostgresIntakeAttachmentAuthority(db);
        var foreign = fixture.Capability with { SessionId = Guid.NewGuid() };

        var status = await authority.ValidateSubmissionAttachmentsAsync(
            foreign, fixture.ClientSubmissionId, [fixture.AttachmentId],
            MaxDurationSeconds, MaxBytesPerAttachment, CancellationToken.None);

        Assert.Equal(IntakeAttachmentValidationStatus.InvalidBinding, status);
    }

    [IntakePostgresFact]
    public async Task ForeignClientSubmission_IsInvalidBinding()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var fixture = await SeedBindingAsync(db);
        var authority = new PostgresIntakeAttachmentAuthority(db);

        var status = await authority.ValidateSubmissionAttachmentsAsync(
            fixture.Capability, Guid.NewGuid(), [fixture.AttachmentId],
            MaxDurationSeconds, MaxBytesPerAttachment, CancellationToken.None);

        Assert.Equal(IntakeAttachmentValidationStatus.InvalidBinding, status);
    }

    [IntakePostgresFact]
    public async Task ConsumedReservation_IsInvalidBinding()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var fixture = await SeedBindingAsync(db, state: IntakeAttachmentState.Attached);
        var authority = new PostgresIntakeAttachmentAuthority(db);

        var status = await authority.ValidateSubmissionAttachmentsAsync(
            fixture.Capability, fixture.ClientSubmissionId, [fixture.AttachmentId],
            MaxDurationSeconds, MaxBytesPerAttachment, CancellationToken.None);

        Assert.Equal(IntakeAttachmentValidationStatus.InvalidBinding, status);
    }

    [IntakePostgresFact]
    public async Task ExpiredReservation_IsInvalidBinding()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var fixture = await SeedBindingAsync(db, expiresIn: TimeSpan.FromMinutes(-5));
        var authority = new PostgresIntakeAttachmentAuthority(db);

        var status = await authority.ValidateSubmissionAttachmentsAsync(
            fixture.Capability, fixture.ClientSubmissionId, [fixture.AttachmentId],
            MaxDurationSeconds, MaxBytesPerAttachment, CancellationToken.None);

        Assert.Equal(IntakeAttachmentValidationStatus.InvalidBinding, status);
    }

    [IntakePostgresFact]
    public async Task OversizedReservation_IsTooLarge()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var fixture = await SeedBindingAsync(db, sizeBytes: MaxBytesPerAttachment + 1, durationSeconds: 60);
        var authority = new PostgresIntakeAttachmentAuthority(db);

        var status = await authority.ValidateSubmissionAttachmentsAsync(
            fixture.Capability, fixture.ClientSubmissionId, [fixture.AttachmentId],
            MaxDurationSeconds, MaxBytesPerAttachment, CancellationToken.None);

        Assert.Equal(IntakeAttachmentValidationStatus.TooLarge, status);
    }

    [IntakePostgresFact]
    public async Task OverDurationReservation_IsTooLarge()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var fixture = await SeedBindingAsync(db, sizeBytes: 1024, durationSeconds: MaxDurationSeconds + 1);
        var authority = new PostgresIntakeAttachmentAuthority(db);

        var status = await authority.ValidateSubmissionAttachmentsAsync(
            fixture.Capability, fixture.ClientSubmissionId, [fixture.AttachmentId],
            MaxDurationSeconds, MaxBytesPerAttachment, CancellationToken.None);

        Assert.Equal(IntakeAttachmentValidationStatus.TooLarge, status);
    }

    [IntakePostgresFact]
    public async Task DuplicateReference_IsInvalidBinding()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var fixture = await SeedBindingAsync(db);
        var authority = new PostgresIntakeAttachmentAuthority(db);

        var status = await authority.ValidateSubmissionAttachmentsAsync(
            fixture.Capability, fixture.ClientSubmissionId, [fixture.AttachmentId, fixture.AttachmentId],
            MaxDurationSeconds, MaxBytesPerAttachment, CancellationToken.None);

        Assert.Equal(IntakeAttachmentValidationStatus.InvalidBinding, status);
    }

    private sealed record BindingFixture(IntakeCapability Capability, Guid ClientSubmissionId, Guid AttachmentId);

    private static async Task<BindingFixture> SeedBindingAsync(
        SwiftAidDbContext db,
        IntakeAttachmentState state = IntakeAttachmentState.Uploaded,
        TimeSpan? expiresIn = null,
        long sizeBytes = 2048,
        int durationSeconds = 45)
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid();
        var capabilityId = Guid.NewGuid();
        var clientSubmissionId = Guid.NewGuid();
        db.IntakeSessions.Add(new IntakeSessionEntity
        {
            SessionId = sessionId,
            ReceiptProofDigest = new byte[32],
            CreatedAtUtc = now,
            LastSeenAtUtc = now
        });
        db.IntakeCapabilities.Add(new IntakeCapabilityEntity
        {
            CapabilityId = capabilityId,
            SessionId = sessionId,
            CapabilityDigest = new byte[32],
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
            SizeBytes = sizeBytes,
            DurationSeconds = durationSeconds,
            State = state,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(expiresIn ?? TimeSpan.FromHours(1))
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var capability = new IntakeCapability(
            capabilityId, sessionId, new byte[32], now.AddHours(1), 10);
        return new BindingFixture(capability, clientSubmissionId, attachmentId);
    }
}
