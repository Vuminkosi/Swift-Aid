using System.Security.Cryptography;
using System.Text.Json;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Cases;
using SwiftAid.Domain.Intake;

namespace SwiftAid.Application.Intake;

public sealed class IntakeSubmissionValidator(
    IIntakeCountryCatalogue countries,
    IIntakeConsentPolicy consentPolicy) : IIntakeSubmissionValidator
{
    private readonly IIntakeCountryCatalogue _countries = countries ?? throw new ArgumentNullException(nameof(countries));
    private readonly IIntakeConsentPolicy _consentPolicy = consentPolicy ?? throw new ArgumentNullException(nameof(consentPolicy));

    public async Task<bool> ValidateAsync(IntakeSubmission submission, IntakeLimits limits, CancellationToken ct)
    {
        IntakeSubmissionRules.Validate(submission, limits);
        if (!await _countries.IsKnownCountryAsync(
            submission.Location.CountryCode,
            submission.Location.CatalogueVersion!,
            ct).ConfigureAwait(false))
        {
            return false;
        }

        return await _consentPolicy.IsCurrentAsync(
            submission.Confirmations,
            submission.OriginalAudioAttachmentIds.Count > 0,
            ct).ConfigureAwait(false);
    }
}

/// <summary>Routes only through configured catchments; never assigns a person or organization.</summary>
public sealed class CoverageRouter(IIntakeCoverageCatalog catalog) : ICoverageRouter
{
    private readonly IIntakeCoverageCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public async Task<CoverageResult> RouteAsync(LocationContext location, CaseKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        IntakeCoverageDecision decision;
        try
        {
            decision = await _catalog.FindAsync(location, kind, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            decision = new IntakeCoverageDecision(IntakeCoverageMatch.Ambiguous, null);
        }
        var status = decision.Match switch
        {
            IntakeCoverageMatch.Eligible when IsValidQueueId(decision.IntakeQueueId) => IntakeRoutingStatus.RoutedForReview,
            IntakeCoverageMatch.Eligible => IntakeRoutingStatus.NeedsRouting,
            IntakeCoverageMatch.None => IntakeRoutingStatus.NoServiceCoverage,
            IntakeCoverageMatch.Ambiguous => IntakeRoutingStatus.NeedsRouting,
            _ => IntakeRoutingStatus.NeedsRouting
        };
        return new CoverageResult(
            status,
            status == IntakeRoutingStatus.RoutedForReview ? decision.IntakeQueueId : null);
    }

    private static bool IsValidQueueId(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

/// <summary>Application implementation; the persistence port must commit receipt/outbox atomically.</summary>
public sealed class IntakeService : IIntakeService
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IIntakeSubmissionStore _submissions;
    private readonly IIntakeDecisionStore _decisions;
    private readonly IIntakePromotionService _promotion;
    private readonly IIntakeAttachmentAuthority _attachments;
    private readonly ICoverageRouter _coverageRouter;
    private readonly IIntakeSubmissionValidator _validator;
    private readonly IntakeLimits _limits;
    private readonly TimeProvider _timeProvider;

    public IntakeService(
        IIntakeSubmissionStore submissions,
        IIntakeDecisionStore decisions,
        IIntakePromotionService promotion,
        IIntakeAttachmentAuthority attachments,
        ICoverageRouter coverageRouter,
        IIntakeSubmissionValidator validator,
        IntakeLimits limits,
        TimeProvider timeProvider)
    {
        _submissions = submissions ?? throw new ArgumentNullException(nameof(submissions));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
        _promotion = promotion ?? throw new ArgumentNullException(nameof(promotion));
        _attachments = attachments ?? throw new ArgumentNullException(nameof(attachments));
        _coverageRouter = coverageRouter ?? throw new ArgumentNullException(nameof(coverageRouter));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<IntakeReceipt> SubmitAsync(
        IntakeSubmission command,
        IntakeCapability capability,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(capability);
        if (capability.CapabilityId == Guid.Empty || capability.SessionId == Guid.Empty ||
            capability.ReceiptProofDigest.Length != 32 || capability.ExpiresAtUtc <= _timeProvider.GetUtcNow() ||
            capability.MaximumSubmissionsPerHour is < 1 ||
            capability.MaximumSubmissionsPerHour > _limits.MaximumSubmissionsPerCapabilityPerHour)
        {
            throw new IntakeSubmissionException(IntakeErrorCode.CapabilityInvalid);
        }

        _limits.Validate();
        if (command.Observations is null || command.OriginalAudioAttachmentIds is null)
        {
            throw new IntakeSubmissionException(IntakeErrorCode.InvalidReport);
        }

        if (command.Narrative is { } narrative && narrative.Length > _limits.MaximumNarrativeCharacters ||
            command.Observations.Count > _limits.MaximumObservations ||
            command.OriginalAudioAttachmentIds.Count > _limits.MaximumAudioAttachments)
        {
            throw new IntakeSubmissionException(IntakeErrorCode.PayloadTooLarge);
        }

        if (!await _validator.ValidateAsync(command, _limits, ct).ConfigureAwait(false))
        {
            throw new IntakeSubmissionException(IntakeErrorCode.InvalidReport);
        }

        command = command with
        {
            CapturedAt = ReportedTimeRules.Normalize(command.CapturedAt),
            Occurrence = ReportedTimeRules.Normalize(command.Occurrence)
        };

        if (command.OriginalAudioAttachmentIds.Count > 0)
        {
            var validation = await _attachments.ValidateSubmissionAttachmentsAsync(
                capability,
                command.ClientSubmissionId,
                command.OriginalAudioAttachmentIds,
                _limits.MaximumAudioDurationSeconds,
                _limits.MaximumAudioBytes,
                ct).ConfigureAwait(false);
            if (validation == IntakeAttachmentValidationStatus.TooLarge)
            {
                throw new IntakeSubmissionException(IntakeErrorCode.PayloadTooLarge);
            }

            if (validation != IntakeAttachmentValidationStatus.Valid)
            {
                throw new IntakeSubmissionException(IntakeErrorCode.CapabilityInvalid);
            }
        }

        var caseKind = command.SubjectScope == IntakeSubjectScope.Human ? CaseKind.Human : CaseKind.Animal;
        var coverage = await _coverageRouter.RouteAsync(command.Location, caseKind, ct).ConfigureAwait(false);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(command, CanonicalJsonOptions);
        var payloadDigest = SHA256.HashData(serialized);
        CryptographicOperations.ZeroMemory(serialized);
        try
        {
            return await _submissions.CommitAsync(
                command,
                capability,
                payloadDigest,
                coverage,
                _timeProvider.GetUtcNow(),
                ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadDigest);
        }
    }

    public Task<IntakeDecisionResult> DecideAsync(
        IntakeDecision command,
        ActorContext actor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);
        if (command.ReceiptId == Guid.Empty || command.ExpectedVersion < 1 ||
            !Enum.IsDefined(command.Decision) || command.ReviewReason is { Length: > 1000 })
        {
            return Task.FromResult(new IntakeDecisionResult(IntakeDecisionStatus.Invalid, null));
        }

        // Promotion executes through the promotion service (registration + promotion link),
        // never through the generic decision path.
        if (command.Decision == IntakeDecisionKind.AcceptIntoCase)
        {
            return _promotion.PromoteAsync(command, actor, ct);
        }

        return _decisions.DecideAsync(command, actor, _timeProvider.GetUtcNow(), ct);
    }
}

/// <summary>Converts a client-owned 256-bit proof to a digest before persistence or lookup.</summary>
public sealed class IntakeSessionEndpointService(
    IIntakeSessionAuthority authority,
    IIntakeReceiptReader receipts,
    TimeProvider timeProvider) : IIntakeSessionEndpointService
{
    private readonly IIntakeSessionAuthority _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    private readonly IIntakeReceiptReader _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<IntakeSessionResponse?> CreateAsync(
        Guid sessionId,
        ReadOnlyMemory<byte> proof,
        CancellationToken ct)
    {
        if (sessionId == Guid.Empty || proof.Length != 32)
        {
            return null;
        }

        var digest = SHA256.HashData(proof.Span);
        try
        {
            return await _authority.CreateOrReuseAsync(
                sessionId,
                digest,
                _timeProvider.GetUtcNow(),
                ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    public async Task<IntakeCapability?> ResolveCapabilityAsync(
        string opaqueCapability,
        Guid sessionId,
        ReadOnlyMemory<byte> proof,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opaqueCapability) || opaqueCapability.Length > 4096 ||
            sessionId == Guid.Empty || proof.Length != 32)
        {
            return null;
        }

        var digest = SHA256.HashData(proof.Span);
        try
        {
            return await _authority.ValidateAsync(
                opaqueCapability,
                sessionId,
                digest,
                _timeProvider.GetUtcNow(),
                ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    public async Task<IntakeReceipt?> GetReceiptAsync(Guid receiptId, ReadOnlyMemory<byte> proof, CancellationToken ct)
    {
        if (receiptId == Guid.Empty || proof.Length != 32)
        {
            return null;
        }

        var digest = SHA256.HashData(proof.Span);
        try
        {
            return await _receipts.GetAsync(receiptId, digest, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }
}
