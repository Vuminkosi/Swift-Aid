using System.Security.Cryptography;

namespace SwiftAid.Infrastructure.Persistence.Jobs;

/// <summary>Outcomes contain a fencing token only for a newly granted lease.</summary>
public sealed record JobClaimTransition(
    JobClaimDisposition Disposition,
    long? FencingToken = null,
    DateTimeOffset? LeaseUntilUtc = null,
    WorkerInboxEntity? Attempt = null);

public enum JobClaimDisposition
{
    Granted,
    AlreadyLeased,
    AlreadyCompleted,
    NotReady,
    AttemptsExhausted
}

public enum JobCompletionDisposition
{
    Accepted,
    DuplicateSameResult,
    DuplicateResultMismatch,
    StaleSourceVersion,
    StaleFencingToken
}

/// <summary>The repository persists Audit atomically with state changes; Result is persisted only when accepted.</summary>
public sealed record JobCompletionTransition(JobCompletionDisposition Disposition, JobResultAuditEntity? Audit = null);

public enum JobFailureDisposition
{
    RetryScheduled,
    Failed,
    DeadLettered,
    StaleFencingToken
}

/// <summary>
/// Pure state transitions used inside a repository transaction. Callers must take the job row lock,
/// insert or read the unique (job, attempt) pending inbox row, validate current descriptors, and persist
/// all changed rows atomically. A unique-key loser must reload that row and never manufacture a fence.
/// </summary>
public static class JobPersistenceTransitions
{
    public static bool TryAcquirePublisherLease(
        JobEntity job,
        IntegrationOutboxEntity outbox,
        string publisherPrincipalId,
        DateTimeOffset nowUtc,
        DateTimeOffset leaseUntilUtc,
        out long publisherFencingToken,
        ReadOnlySpan<byte> currentSourceVersionHash,
        IJobSourceVersionDescriptorRegistry sourceDescriptors,
        IJobResultSchemaValidatorRegistry resultSchemas)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(outbox);
        outbox.ValidateForJob(job);
        ValidateUtc(nowUtc, nameof(nowUtc));
        ValidateUtc(leaseUntilUtc, nameof(leaseUntilUtc));
        ValidatePrincipal(publisherPrincipalId, nameof(publisherPrincipalId));
        JobTypeCapabilityGate.RequireEnabled(job, sourceDescriptors, resultSchemas);
        var sourceSchema = JobSourceVersionDescriptorResolver.Require(sourceDescriptors, job.JobType, job.SourceVersionDescriptorSchemaVersion);
        JobSourceVersionDescriptorCodec.ValidateStored(job.JobType, job.SourceVersionDescriptorSchemaVersion,
            job.SourceVersionDescriptorBytes, job.SourceVersionHash, sourceSchema);
        if (leaseUntilUtc <= nowUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseUntilUtc), "A publisher lease must expire in the future.");
        }

        publisherFencingToken = 0;
        if (!JobSourceVersionHash.Matches(job.SourceVersionHash, currentSourceVersionHash) ||
            job.State is not (JobState.PendingPublication or JobState.RetryScheduled) ||
            job.AvailableAtUtc > nowUtc || outbox.AttemptNumber != job.AttemptCount ||
            outbox.PublishedAtUtc.HasValue || outbox.AvailableAtUtc > nowUtc ||
            (outbox.PublisherLeaseUntilUtc is { } currentLease && currentLease > nowUtc))
        {
            return false;
        }

        outbox.PublisherFencingToken = checked(outbox.PublisherFencingToken + 1);
        outbox.PublisherLeaseOwner = publisherPrincipalId;
        outbox.PublisherLeaseUntilUtc = leaseUntilUtc;
        outbox.PublishAttemptCount = checked(outbox.PublishAttemptCount + 1);
        outbox.Validate();
        publisherFencingToken = outbox.PublisherFencingToken;
        return true;
    }

    public static bool RecordPublisherAcknowledgement(
        JobEntity job,
        IntegrationOutboxEntity outbox,
        long fencingToken,
        string streamName,
        ulong streamSequence,
        DateTimeOffset acknowledgedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(outbox);
        outbox.ValidateForJob(job);
        ValidateUtc(acknowledgedAtUtc, nameof(acknowledgedAtUtc));
        if (string.IsNullOrWhiteSpace(streamName) ||
            !string.Equals(streamName, "SWIFTAID_JOBS", StringComparison.Ordinal) ||
            streamName.Length > JobPersistenceLimits.MaximumSubjectLength ||
            streamName.Any(char.IsControl) || streamSequence < 1)
        {
            throw new ArgumentException("A bounded stream name and positive PubAck sequence are required.");
        }

        if (outbox.PublishedAtUtc.HasValue)
        {
            return string.Equals(outbox.AcknowledgedStream, streamName, StringComparison.Ordinal) &&
                outbox.AcknowledgedSequence == (decimal)streamSequence &&
                job.State is JobState.Published or JobState.Leased or JobState.Completed;
        }

        if (job.State is not (JobState.PendingPublication or JobState.RetryScheduled) ||
            outbox.AttemptNumber != job.AttemptCount || fencingToken < 1 || outbox.PublisherFencingToken != fencingToken ||
            outbox.PublisherLeaseUntilUtc is not { } leaseUntil || leaseUntil <= acknowledgedAtUtc)
        {
            return false;
        }

        outbox.PublishedAtUtc = acknowledgedAtUtc;
        outbox.AcknowledgedStream = streamName;
        outbox.AcknowledgedSequence = (decimal)streamSequence;
        outbox.PublisherLeaseOwner = null;
        outbox.PublisherLeaseUntilUtc = null;
        job.State = JobState.Published;
        outbox.Validate();
        job.Validate();
        return true;
    }

    public static JobDeadLetterEntity CreateDeadLetter(
        JobEntity job,
        WorkerInboxEntity terminalAttempt,
        Guid deadLetterId,
        Guid deadLetterMessageId,
        IJobSourceVersionDescriptorRegistry sourceDescriptors,
        IJobResultSchemaValidatorRegistry resultSchemas)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(terminalAttempt);
        job.Validate();
        terminalAttempt.Validate();
        JobTypeCapabilityGate.RequireEnabled(job, sourceDescriptors, resultSchemas);
        var sourceSchema = JobSourceVersionDescriptorResolver.Require(sourceDescriptors, job.JobType, job.SourceVersionDescriptorSchemaVersion);
        JobSourceVersionDescriptorCodec.ValidateStored(job.JobType, job.SourceVersionDescriptorSchemaVersion,
            job.SourceVersionDescriptorBytes, job.SourceVersionHash, sourceSchema);
        if (job.JobId != terminalAttempt.JobId || job.AttemptCount != terminalAttempt.AttemptNumber ||
            job.State is not (JobState.Failed or JobState.DeadLettered) ||
            terminalAttempt.State is not (WorkerAttemptState.Failed or WorkerAttemptState.DeadLettered) ||
            terminalAttempt.FailureCode is null ||
            !string.Equals(job.LastFailureCode, terminalAttempt.FailureCode, StringComparison.Ordinal) ||
            terminalAttempt.FinishedAtUtc is not { } failedAtUtc)
        {
            throw new InvalidOperationException("A dead-letter row requires an atomically classified terminal attempt.");
        }

        if (deadLetterId == Guid.Empty || deadLetterMessageId == Guid.Empty)
        {
            throw new ArgumentException("Stable dead-letter identifiers must be non-empty.");
        }

        var envelope = new JobDeadLetterEnvelope(
            JobDeadLetterEnvelope.CurrentSchemaVersion,
            deadLetterMessageId,
            job.JobId,
            job.JobType,
            terminalAttempt.AttemptNumber,
            terminalAttempt.FailureCode,
            failedAtUtc).Validate();
        var entity = new JobDeadLetterEntity
        {
            DeadLetterId = deadLetterId,
            JobId = job.JobId,
            TerminalAttemptNumber = terminalAttempt.AttemptNumber,
            MessageId = deadLetterMessageId,
            JobType = job.JobType,
            FailureCode = terminalAttempt.FailureCode,
            SchemaVersion = JobDeadLetterEnvelope.CurrentSchemaVersion,
            FailedAtUtc = failedAtUtc,
            Subject = JobDeadLetterSubject.For(job.JobType),
            EnvelopeUtf8 = envelope.SerializeToUtf8(),
            AvailableAtUtc = failedAtUtc
        };
        entity.Validate();
        return entity;
    }

    public static bool TryAcquireDeadLetterPublisherLease(
        JobEntity job,
        JobDeadLetterEntity deadLetter,
        string publisherPrincipalId,
        DateTimeOffset nowUtc,
        DateTimeOffset leaseUntilUtc,
        out long publisherFencingToken,
        IJobSourceVersionDescriptorRegistry sourceDescriptors,
        IJobResultSchemaValidatorRegistry resultSchemas)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(deadLetter);
        deadLetter.Validate();
        if (job.JobId != deadLetter.JobId || job.JobType != deadLetter.JobType)
        {
            throw new InvalidOperationException("The dead-letter job identity does not match its authoritative job.");
        }
        JobTypeCapabilityGate.RequireEnabled(job, sourceDescriptors, resultSchemas);
        ValidateUtc(nowUtc, nameof(nowUtc));
        ValidateUtc(leaseUntilUtc, nameof(leaseUntilUtc));
        ValidatePrincipal(publisherPrincipalId, nameof(publisherPrincipalId));
        if (leaseUntilUtc <= nowUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseUntilUtc), "A dead-letter lease must expire in the future.");
        }

        publisherFencingToken = 0;
        if (deadLetter.PublishedAtUtc.HasValue || deadLetter.AvailableAtUtc > nowUtc ||
            (deadLetter.PublisherLeaseUntilUtc is { } currentLease && currentLease > nowUtc))
        {
            return false;
        }

        deadLetter.PublisherFencingToken = checked(deadLetter.PublisherFencingToken + 1);
        deadLetter.PublisherLeaseOwner = publisherPrincipalId;
        deadLetter.PublisherLeaseUntilUtc = leaseUntilUtc;
        deadLetter.PublishAttemptCount = checked(deadLetter.PublishAttemptCount + 1);
        deadLetter.Validate();
        publisherFencingToken = deadLetter.PublisherFencingToken;
        return true;
    }

    public static bool RecordDeadLetterPublisherAcknowledgement(
        JobDeadLetterEntity deadLetter,
        long fencingToken,
        string streamName,
        ulong streamSequence,
        DateTimeOffset acknowledgedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(deadLetter);
        deadLetter.Validate();
        ValidateUtc(acknowledgedAtUtc, nameof(acknowledgedAtUtc));
        if (string.IsNullOrWhiteSpace(streamName) ||
            !string.Equals(streamName, JobDeadLetterEnvelope.StreamName, StringComparison.Ordinal) ||
            streamName.Length > JobPersistenceLimits.MaximumSubjectLength ||
            streamName.Any(char.IsControl) || streamSequence < 1)
        {
            throw new ArgumentException("A bounded stream name and positive PubAck sequence are required.");
        }

        if (deadLetter.PublishedAtUtc.HasValue)
        {
            return string.Equals(deadLetter.AcknowledgedStream, streamName, StringComparison.Ordinal) &&
                deadLetter.AcknowledgedSequence == (decimal)streamSequence;
        }

        if (fencingToken < 1 || deadLetter.PublisherFencingToken != fencingToken ||
            deadLetter.PublisherLeaseUntilUtc is not { } leaseUntil || leaseUntil <= acknowledgedAtUtc)
        {
            return false;
        }

        deadLetter.PublishedAtUtc = acknowledgedAtUtc;
        deadLetter.AcknowledgedStream = streamName;
        deadLetter.AcknowledgedSequence = (decimal)streamSequence;
        deadLetter.PublisherLeaseOwner = null;
        deadLetter.PublisherLeaseUntilUtc = null;
        deadLetter.Validate();
        return true;
    }

    public static JobClaimTransition TryClaim(
        JobEntity job,
        IntegrationOutboxEntity outbox,
        WorkerInboxEntity? inbox,
        string workerPrincipalId,
        DateTimeOffset nowUtc,
        DateTimeOffset leaseUntilUtc,
        ReadOnlySpan<byte> currentSourceVersionHash,
        IJobSourceVersionDescriptorRegistry sourceDescriptors,
        IJobResultSchemaValidatorRegistry resultSchemas)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(outbox);
        outbox.ValidateForJob(job);
        ValidateUtc(nowUtc, nameof(nowUtc));
        ValidateUtc(leaseUntilUtc, nameof(leaseUntilUtc));
        ValidatePrincipal(workerPrincipalId, nameof(workerPrincipalId));
        JobTypeCapabilityGate.RequireEnabled(job, sourceDescriptors, resultSchemas);
        JobSourceVersionDescriptorCodec.ValidateStored(job.JobType, job.SourceVersionDescriptorSchemaVersion,
            job.SourceVersionDescriptorBytes, job.SourceVersionHash,
            JobSourceVersionDescriptorResolver.Require(sourceDescriptors, job.JobType, job.SourceVersionDescriptorSchemaVersion));
        if (leaseUntilUtc <= nowUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseUntilUtc), "A worker lease must expire in the future.");
        }

        if (job.State == JobState.Completed)
        {
            return new JobClaimTransition(JobClaimDisposition.AlreadyCompleted);
        }

        if (job.State == JobState.Leased && job.WorkerLeaseUntilUtc is { } activeLease && activeLease > nowUtc)
        {
            return new JobClaimTransition(JobClaimDisposition.AlreadyLeased);
        }

        if (job.AttemptCount > job.MaximumAttempts)
        {
            return new JobClaimTransition(JobClaimDisposition.AttemptsExhausted);
        }

        if (!JobSourceVersionHash.Matches(job.SourceVersionHash, currentSourceVersionHash))
        {
            return new JobClaimTransition(JobClaimDisposition.NotReady);
        }

        inbox ??= WorkerInboxEntity.CreatePending(job, outbox, nowUtc);

        if (job.State != JobState.Published || !outbox.PublishedAtUtc.HasValue ||
            outbox.AttemptNumber != job.AttemptCount || outbox.MessageId != inbox.MessageId ||
            outbox.JobId != job.JobId || job.AvailableAtUtc > nowUtc || inbox.JobId != job.JobId || inbox.MessageId == Guid.Empty ||
            inbox.AttemptNumber != job.AttemptCount || inbox.State != WorkerAttemptState.Pending ||
            inbox.LeaseUntilUtc.HasValue || inbox.FencingToken != 0 || inbox.WorkerPrincipalId is not null)
        {
            return new JobClaimTransition(JobClaimDisposition.NotReady);
        }

        var nextFencingToken = checked(job.CurrentFencingToken + 1);
        job.State = JobState.Leased;
        job.CurrentFencingToken = nextFencingToken;
        job.WorkerLeaseUntilUtc = leaseUntilUtc;
        job.WorkerPrincipalId = workerPrincipalId;
        inbox.State = WorkerAttemptState.Leased;
        inbox.FencingToken = nextFencingToken;
        inbox.LeaseUntilUtc = leaseUntilUtc;
        inbox.WorkerPrincipalId = workerPrincipalId;
        job.Validate();
        inbox.Validate();

        return new JobClaimTransition(JobClaimDisposition.Granted, nextFencingToken, leaseUntilUtc, inbox);
    }

    /// <summary>Maps a unique-inbox insert race to a generic safe outcome after reloading under the job lock.</summary>
    public static JobClaimTransition ResolveDuplicateClaim(JobEntity job, WorkerInboxEntity persistedAttempt, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(persistedAttempt);
        ValidateUtc(nowUtc, nameof(nowUtc));
        if (job.JobId != persistedAttempt.JobId)
        {
            return new JobClaimTransition(JobClaimDisposition.NotReady);
        }
        if (job.State == JobState.Leased && job.WorkerLeaseUntilUtc is { } until && until > nowUtc &&
            persistedAttempt.State == WorkerAttemptState.Leased && persistedAttempt.LeaseUntilUtc == until)
        {
            return new JobClaimTransition(JobClaimDisposition.AlreadyLeased);
        }
        if (job.State == JobState.Completed && persistedAttempt.State == WorkerAttemptState.Completed)
        {
            return new JobClaimTransition(JobClaimDisposition.AlreadyCompleted);
        }
        return new JobClaimTransition(JobClaimDisposition.NotReady);
    }

    public static bool TryRenewWorkerLease(
        JobEntity job,
        WorkerInboxEntity inbox,
        string workerPrincipalId,
        long fencingToken,
        DateTimeOffset nowUtc,
        DateTimeOffset renewedUntilUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(inbox);
        ValidateUtc(nowUtc, nameof(nowUtc));
        ValidateUtc(renewedUntilUtc, nameof(renewedUntilUtc));
        ValidatePrincipal(workerPrincipalId, nameof(workerPrincipalId));
        if (renewedUntilUtc <= nowUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(renewedUntilUtc), "A renewed lease must expire in the future.");
        }

        if (!OwnsActiveWorkerLease(job, inbox, workerPrincipalId, fencingToken, nowUtc))
        {
            return false;
        }

        job.WorkerLeaseUntilUtc = renewedUntilUtc;
        inbox.LeaseUntilUtc = renewedUntilUtc;
        job.Validate();
        inbox.Validate();
        return true;
    }

    public static JobCompletionTransition Complete(
        JobEntity job,
        WorkerInboxEntity inbox,
        JobResultEntity acceptedResult,
        JobResultEntity? existingResultForAttempt,
        string workerPrincipalId,
        long fencingToken,
        ReadOnlySpan<byte> currentSourceVersionHash,
        DateTimeOffset acceptedAtUtc,
        IJobSourceVersionDescriptorRegistry sourceDescriptors,
        IJobResultSchemaValidatorRegistry resultSchemas,
        int maximumResultBytes = JobPersistenceLimits.DefaultMaximumResultBytes)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(acceptedResult);
        ValidateUtc(acceptedAtUtc, nameof(acceptedAtUtc));
        ValidatePrincipal(workerPrincipalId, nameof(workerPrincipalId));
        if (!string.Equals(acceptedResult.WorkerPrincipalId, workerPrincipalId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The result worker attribution must match the authenticated worker principal.", nameof(acceptedResult));
        }

        JobTypeCapabilityGate.RequireEnabled(job, sourceDescriptors, resultSchemas);
        var descriptorSchema = JobSourceVersionDescriptorResolver.Require(
            sourceDescriptors, job.JobType, job.SourceVersionDescriptorSchemaVersion);
        JobSourceVersionDescriptorCodec.ValidateStored(job.JobType, job.SourceVersionDescriptorSchemaVersion,
            job.SourceVersionDescriptorBytes, job.SourceVersionHash, descriptorSchema);
        var payloadHash = GetBoundedExactPayloadHash(acceptedResult, maximumResultBytes);
        if (!JobSourceVersionHash.IsValid(currentSourceVersionHash))
        {
            throw new ArgumentException("A current source-version hash is required for result completion.", nameof(currentSourceVersionHash));
        }

        if (job.State == JobState.Completed && job.CurrentResultAttemptNumber == acceptedResult.AttemptNumber)
        {
            var duplicateMatches = acceptedResult.JobId == job.JobId && existingResultForAttempt is not null &&
                existingResultForAttempt.JobId == job.JobId &&
                existingResultForAttempt.AttemptNumber == acceptedResult.AttemptNumber &&
                existingResultForAttempt.ResultSchemaVersion == job.ExpectedResultSchemaVersion &&
                acceptedResult.ResultSchemaVersion == job.ExpectedResultSchemaVersion &&
                existingResultForAttempt.FencingToken == acceptedResult.FencingToken &&
                JobSourceVersionHash.Matches(existingResultForAttempt.SourceVersionHash, acceptedResult.SourceVersionHash) &&
                JobSourceVersionHash.Matches(existingResultForAttempt.PayloadSha256, payloadHash) &&
                JobSourceVersionHash.Matches(acceptedResult.PayloadSha256, payloadHash);
            if (duplicateMatches)
            {
                return new JobCompletionTransition(JobCompletionDisposition.DuplicateSameResult);
            }

            var duplicateAudit = CreateResultAudit(job, inbox, workerPrincipalId, fencingToken,
                acceptedAtUtc, currentSourceVersionHash, payloadHash, JobResultAuditDecisionCodes.DuplicateHashMismatch);
            return new JobCompletionTransition(JobCompletionDisposition.DuplicateResultMismatch, duplicateAudit);
        }

        if (!OwnsActiveWorkerLease(job, inbox, workerPrincipalId, fencingToken, acceptedAtUtc) ||
            acceptedResult.JobId != job.JobId || acceptedResult.AttemptNumber != inbox.AttemptNumber ||
            acceptedResult.FencingToken != fencingToken)
        {
            var fencingAudit = CreateResultAudit(job, inbox, workerPrincipalId, fencingToken,
                acceptedAtUtc, currentSourceVersionHash, payloadHash, JobResultAuditDecisionCodes.StaleFencingToken);
            return new JobCompletionTransition(JobCompletionDisposition.StaleFencingToken, fencingAudit);
        }

        if (!JobSourceVersionHash.Matches(job.SourceVersionHash, currentSourceVersionHash) ||
            !JobSourceVersionHash.Matches(acceptedResult.SourceVersionHash, job.SourceVersionHash))
        {
            var resultAudit = CreateResultAudit(job, inbox, workerPrincipalId, fencingToken,
                acceptedAtUtc, currentSourceVersionHash, payloadHash, JobResultAuditDecisionCodes.StaleSourceVersion);
            FinishStale(job, inbox, acceptedAtUtc);
            return new JobCompletionTransition(JobCompletionDisposition.StaleSourceVersion, resultAudit);
        }

        acceptedResult.Validate(maximumResultBytes);
        if (acceptedResult.ResultSchemaVersion != job.ExpectedResultSchemaVersion)
        {
            throw new UnsupportedJobResultSchemaException(job.JobType, acceptedResult.ResultSchemaVersion);
        }
        _ = JobResultContent.Validate(job.JobType, acceptedResult.ResultSchemaVersion,
            acceptedResult.PayloadUtf8, new JobPersistenceOptions { MaximumResultBytes = maximumResultBytes }, resultSchemas);

        if (acceptedResult.AcceptedAtUtc != acceptedAtUtc)
        {
            throw new InvalidOperationException("The accepted result timestamp must match the transaction timestamp.");
        }

        acceptedResult.Validate();
        job.State = JobState.Completed;
        job.CurrentResultAttemptNumber = acceptedResult.AttemptNumber;
        job.CompletedAtUtc = acceptedAtUtc;
        job.WorkerLeaseUntilUtc = null;
        job.WorkerPrincipalId = null;
        job.LastFailureCode = null;
        inbox.State = WorkerAttemptState.Completed;
        inbox.LeaseUntilUtc = null;
        inbox.FinishedAtUtc = acceptedAtUtc;
        job.Validate();
        inbox.Validate();
        return new JobCompletionTransition(JobCompletionDisposition.Accepted);
    }

    public static JobFailureDisposition Fail(
        JobEntity job,
        WorkerInboxEntity inbox,
        string workerPrincipalId,
        long fencingToken,
        string failureCode,
        DateTimeOffset nowUtc,
        DateTimeOffset? retryAtUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(inbox);
        ValidateUtc(nowUtc, nameof(nowUtc));
        ValidatePrincipal(workerPrincipalId, nameof(workerPrincipalId));
        if (!JobFailureCodes.IsAllowed(failureCode))
        {
            throw new ArgumentException("A gateway-allowlisted failure code is required.", nameof(failureCode));
        }

        if (!OwnsActiveWorkerLease(job, inbox, workerPrincipalId, fencingToken, nowUtc))
        {
            return JobFailureDisposition.StaleFencingToken;
        }

        var retryable = JobFailureCodes.IsRetryable(failureCode);
        if (retryable && job.AttemptCount < job.MaximumAttempts)
        {
            if (retryAtUtc is not { } retryAt || retryAt <= nowUtc)
            {
                throw new ArgumentException("A future retry instant is required for retryable failures.", nameof(retryAtUtc));
            }

            ValidateUtc(retryAt, nameof(retryAtUtc));
            job.State = JobState.RetryScheduled;
            job.AvailableAtUtc = retryAt;
            inbox.State = WorkerAttemptState.RetryScheduled;
            inbox.RetryAtUtc = retryAt;
        }
        else
        {
            if (retryAtUtc.HasValue)
            {
                throw new ArgumentException("A retry instant is allowed only while another retry is available.", nameof(retryAtUtc));
            }

            job.State = retryable ? JobState.DeadLettered : JobState.Failed;
            job.CompletedAtUtc = null;
            inbox.State = retryable ? WorkerAttemptState.DeadLettered : WorkerAttemptState.Failed;
            inbox.FinishedAtUtc = nowUtc;
            inbox.RetryAtUtc = null;
        }

        job.LastFailureCode = failureCode;
        job.WorkerLeaseUntilUtc = null;
        job.WorkerPrincipalId = null;
        inbox.LeaseUntilUtc = null;
        inbox.FailureCode = failureCode;
        job.Validate();
        inbox.Validate();
        return job.State switch
        {
            JobState.RetryScheduled => JobFailureDisposition.RetryScheduled,
            JobState.DeadLettered => JobFailureDisposition.DeadLettered,
            _ => JobFailureDisposition.Failed
        };
    }

    public static bool ExpireWorkerLease(JobEntity job, WorkerInboxEntity inbox, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(inbox);
        ValidateUtc(nowUtc, nameof(nowUtc));
        if (job.State != JobState.Leased || inbox.State != WorkerAttemptState.Leased ||
            job.WorkerLeaseUntilUtc is not { } jobLease || inbox.LeaseUntilUtc is not { } attemptLease ||
            jobLease != attemptLease || jobLease > nowUtc || job.CurrentFencingToken != inbox.FencingToken)
        {
            return false;
        }

        job.WorkerLeaseUntilUtc = null;
        job.WorkerPrincipalId = null;
        inbox.LeaseUntilUtc = null;
        inbox.FailureCode = JobFailureCodes.WorkerLeaseExpired;
        job.LastFailureCode = JobFailureCodes.WorkerLeaseExpired;
        if (job.AttemptCount >= job.MaximumAttempts)
        {
            job.State = JobState.DeadLettered;
            inbox.State = WorkerAttemptState.DeadLettered;
            inbox.FinishedAtUtc = nowUtc;
        }
        else
        {
            job.State = JobState.RetryScheduled;
            job.AvailableAtUtc = nowUtc;
            inbox.State = WorkerAttemptState.RetryScheduled;
            inbox.RetryAtUtc = nowUtc;
        }

        job.Validate();
        inbox.Validate();
        return true;
    }

    private static bool OwnsActiveWorkerLease(
        JobEntity job,
        WorkerInboxEntity inbox,
        string workerPrincipalId,
        long fencingToken,
        DateTimeOffset nowUtc) =>
        fencingToken > 0 && job.State == JobState.Leased && inbox.State == WorkerAttemptState.Leased &&
        job.JobId == inbox.JobId && job.WorkerLeaseUntilUtc is { } jobLease && jobLease > nowUtc &&
        inbox.LeaseUntilUtc == jobLease && job.CurrentFencingToken == fencingToken && inbox.FencingToken == fencingToken &&
        string.Equals(job.WorkerPrincipalId, workerPrincipalId, StringComparison.Ordinal) &&
        string.Equals(inbox.WorkerPrincipalId, workerPrincipalId, StringComparison.Ordinal);

    private static void FinishStale(JobEntity job, WorkerInboxEntity inbox, DateTimeOffset nowUtc)
    {
        job.State = JobState.Stale;
        job.WorkerLeaseUntilUtc = null;
        job.WorkerPrincipalId = null;
        job.LastFailureCode = JobFailureCodes.InputVersionStale;
        inbox.State = WorkerAttemptState.Stale;
        inbox.LeaseUntilUtc = null;
        inbox.FinishedAtUtc = nowUtc;
        inbox.FailureCode = JobFailureCodes.InputVersionStale;
        job.Validate();
        inbox.Validate();
    }

    private static JobResultAuditEntity CreateResultAudit(
        JobEntity job,
        WorkerInboxEntity inbox,
        string workerPrincipalId,
        long fencingToken,
        DateTimeOffset recordedAtUtc,
        ReadOnlySpan<byte> currentSourceVersionHash,
        ReadOnlySpan<byte> resultHash,
        string decisionCode)
    {
        if (!JobSourceVersionHash.IsValid(currentSourceVersionHash) || !JobSourceVersionHash.IsValid(resultHash))
        {
            throw new ArgumentException("Valid source and exact result hashes are required for result audit.");
        }

        var audit = new JobResultAuditEntity
        {
            AuditId = Guid.NewGuid(),
            JobId = job.JobId,
            AttemptNumber = inbox.AttemptNumber,
            FencingToken = Math.Max(0, fencingToken),
            SourceVersionHash = job.SourceVersionHash.ToArray(),
            CurrentSourceVersionHash = currentSourceVersionHash.ToArray(),
            ResultHash = resultHash.ToArray(),
            DecisionCode = decisionCode,
            WorkerPrincipalId = workerPrincipalId,
            RecordedAtUtc = recordedAtUtc
        };
        audit.Validate();
        return audit;
    }

    private static byte[] GetBoundedExactPayloadHash(JobResultEntity result, int maximumResultBytes)
    {
        if (maximumResultBytes is < 1 or > JobPersistenceLimits.DefaultMaximumResultBytes ||
            result.PayloadUtf8.Length is 0 || result.PayloadUtf8.Length > maximumResultBytes)
        {
            throw new ArgumentException("A non-empty result within the configured byte limit is required.", nameof(result));
        }

        return SHA256.HashData(result.PayloadUtf8);
    }

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A non-default UTC instant is required.", parameterName);
        }
    }

    private static void ValidatePrincipal(string principalId, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(principalId) || principalId.Length > JobPersistenceLimits.MaximumPrincipalIdLength ||
            principalId.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded authenticated workload principal identifier is required.", parameterName);
        }
    }
}
