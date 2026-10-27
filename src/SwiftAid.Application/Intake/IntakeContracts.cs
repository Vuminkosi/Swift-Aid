using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Cases;
using SwiftAid.Domain.Intake;

namespace SwiftAid.Application.Intake;

public enum IntakeErrorCode
{
    CapabilityInvalid,
    RateLimited,
    PayloadTooLarge,
    SubmissionConflict,
    AlreadyPromoted,
    InvalidReport
}

public sealed class IntakeSubmissionException(IntakeErrorCode code) : Exception("The intake submission could not be completed.")
{
    public IntakeErrorCode Code { get; } = code;
}

public enum IntakeDecisionKind
{
    RouteForReview,
    AcceptIntoCase,
    LinkAsEvidence,
    Decline
}

public sealed record IntakeDecision(
    Guid ReceiptId,
    long ExpectedVersion,
    IntakeDecisionKind Decision,
    string? ReviewReason);

public enum IntakeDecisionStatus
{
    Applied,
    NotFoundOrNotAuthorized,
    Conflict,
    Invalid
}

public sealed record IntakeDecisionResult(IntakeDecisionStatus Status, IntakeReceipt? Receipt);

public sealed record IntakeCapability(
    Guid CapabilityId,
    Guid SessionId,
    ReadOnlyMemory<byte> ReceiptProofDigest,
    DateTimeOffset ExpiresAtUtc,
    int MaximumSubmissionsPerHour);

/// <summary>Internal routing decision; QueueId is never returned to the reporter.</summary>
public sealed record CoverageResult(IntakeRoutingStatus Status, string? IntakeQueueId = null);

/// <summary>Authenticated submission service. Capability scope is server-issued and contains no staff identity.</summary>
public interface IIntakeService
{
    Task<IntakeReceipt> SubmitAsync(
        IntakeSubmission command,
        IntakeCapability capability,
        CancellationToken ct);

    Task<IntakeDecisionResult> DecideAsync(
        IntakeDecision command,
        ActorContext actor,
        CancellationToken ct);
}

public interface ICoverageRouter
{
    Task<CoverageResult> RouteAsync(LocationContext location, CaseKind kind, CancellationToken ct);
}

/// <summary>
/// The trusted implementation stores only receipt-proof/capability digests and enforces capability quota,
/// expiry and renewal idempotency in PostgreSQL. Raw proof is never persisted.
/// </summary>
public interface IIntakeSessionAuthority
{
    Task<IntakeSessionResponse?> CreateOrReuseAsync(
        Guid sessionId,
        ReadOnlyMemory<byte> receiptProofDigest,
        DateTimeOffset nowUtc,
        CancellationToken ct);

    Task<IntakeCapability?> ValidateAsync(
        string opaqueCapability,
        Guid sessionId,
        ReadOnlyMemory<byte> receiptProofDigest,
        DateTimeOffset nowUtc,
        CancellationToken ct);
}

/// <summary>
/// Atomic idempotency boundary for private intake and its receipt/outbox. Within a session, the same
/// client submission ID, session proof digest and payload digest return the original receipt; the same
/// key with a different payload digest conflicts. A capability renewal never creates a second intake.
/// </summary>
public interface IIntakeSubmissionStore
{
    Task<IntakeReceipt> CommitAsync(
        IntakeSubmission submission,
        IntakeCapability capability,
        ReadOnlyMemory<byte> canonicalPayloadDigest,
        CoverageResult coverage,
        DateTimeOffset receivedAtUtc,
        CancellationToken ct);
}

public interface IIntakeDecisionStore
{
    Task<IntakeDecisionResult> DecideAsync(
        IntakeDecision decision,
        ActorContext actor,
        DateTimeOffset nowUtc,
        CancellationToken ct);
}

/// <summary>
/// Receipt proof grants this minimal projection only; no case lookup is exposed. Implementations
/// compare proof digests in constant time and return the same generic not-found result on mismatch.
/// </summary>
public interface IIntakeReceiptReader
{
    Task<IntakeReceipt?> GetAsync(
        Guid receiptId,
        ReadOnlyMemory<byte> receiptProofDigest,
        CancellationToken ct);
}

public interface IIntakeSessionEndpointService
{
    Task<IntakeSessionResponse?> CreateAsync(Guid sessionId, ReadOnlyMemory<byte> proof, CancellationToken ct);
    Task<IntakeCapability?> ResolveCapabilityAsync(
        string opaqueCapability,
        Guid sessionId,
        ReadOnlyMemory<byte> proof,
        CancellationToken ct);
    Task<IntakeReceipt?> GetReceiptAsync(Guid receiptId, ReadOnlyMemory<byte> proof, CancellationToken ct);
}

public interface IIntakeSubmissionValidator
{
    Task<bool> ValidateAsync(IntakeSubmission submission, IntakeLimits limits, CancellationToken ct);
}

public interface IIntakeCountryCatalogue
{
    Task<bool> IsKnownCountryAsync(string countryCode, string catalogueVersion, CancellationToken ct);
}

public interface IIntakePublicMetadataProvider
{
    PublicIntakeMetadata GetMetadata();
    Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken ct);
}

public interface IIntakeConsentPolicy
{
    Task<bool> IsCurrentAsync(IntakeConfirmations confirmations, bool includesAudio, CancellationToken ct);
}

public interface IIntakeCoverageCatalog
{
    Task<IntakeCoverageDecision> FindAsync(LocationContext location, CaseKind kind, CancellationToken ct);
}

public enum IntakeCoverageMatch
{
    Eligible,
    None,
    Ambiguous
}

public sealed record IntakeCoverageDecision(IntakeCoverageMatch Match, string? IntakeQueueId);

public interface IIntakeAttachmentAuthority
{
    Task<IntakeAttachmentValidationStatus> ValidateSubmissionAttachmentsAsync(
        IntakeCapability capability,
        Guid clientSubmissionId,
        IReadOnlyList<Guid> attachmentIds,
        int maximumDurationSeconds,
        long maximumBytesPerAttachment,
        CancellationToken ct);
}

public enum IntakeAttachmentValidationStatus
{
    Valid,
    InvalidBinding,
    TooLarge
}
