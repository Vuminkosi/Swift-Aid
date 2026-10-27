using System.Security.Cryptography;
using SwiftAid.Contracts.Jobs;

namespace SwiftAid.Infrastructure.Persistence.Jobs;

/// <summary>Authoritative PostgreSQL state for one logical background job.</summary>
public sealed class JobEntity
{
    public Guid JobId { get; set; }
    public required string OrganizationId { get; set; }
    public JobType JobType { get; set; }
    public Guid InputSnapshotId { get; set; }
    public Guid CorrelationId { get; set; }
    public int EnvelopeSchemaVersion { get; set; }
    public int SourceVersionDescriptorSchemaVersion { get; set; }
    public byte[] SourceVersionDescriptorBytes { get; set; } = [];
    public int ExpectedResultSchemaVersion { get; set; }
    public byte[] SourceVersionHash { get; set; } = [];
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public JobState State { get; set; }
    public int AttemptCount { get; set; }
    public int MaximumAttempts { get; set; }
    public long CurrentFencingToken { get; set; }
    public DateTimeOffset? WorkerLeaseUntilUtc { get; set; }
    public string? WorkerPrincipalId { get; set; }
    public int? CurrentResultAttemptNumber { get; set; }
    public string? LastFailureCode { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }

    public void Validate()
    {
        if (JobId == Guid.Empty || InputSnapshotId == Guid.Empty || CorrelationId == Guid.Empty)
        {
            throw new InvalidOperationException("Job and snapshot identifiers must be non-empty.");
        }

        if (string.IsNullOrWhiteSpace(OrganizationId) ||
            !string.Equals(OrganizationId, OrganizationId.Trim(), StringComparison.Ordinal) ||
            OrganizationId.Length > JobPersistenceLimits.MaximumOrganizationIdLength ||
            OrganizationId.Any(char.IsControl))
        {
            throw new InvalidOperationException("A canonical opaque organization identifier is required.");
        }

        if (!Enum.IsDefined(JobType) || JobType == JobType.Surveillance ||
            EnvelopeSchemaVersion != JobEnvelope.CurrentSchemaVersion || SourceVersionDescriptorSchemaVersion < 1 ||
            ExpectedResultSchemaVersion < 1 ||
            !Enum.IsDefined(State))
        {
            throw new InvalidOperationException("The job type or envelope schema version is unsupported.");
        }

        if (SourceVersionHash.Length != JobSourceVersionHash.HashLength || SourceVersionDescriptorBytes.Length == 0 ||
            !CryptographicOperations.FixedTimeEquals(SourceVersionHash, SHA256.HashData(SourceVersionDescriptorBytes)) ||
            CreatedAtUtc == default || CreatedAtUtc.Offset != TimeSpan.Zero ||
            AvailableAtUtc == default || AvailableAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Job source-version and timestamp metadata is invalid.");
        }

        if (AttemptCount < 0 || MaximumAttempts is < 1 or > JobPersistenceLimits.MaximumAttempts ||
            AttemptCount > MaximumAttempts || CurrentFencingToken < 0)
        {
            throw new InvalidOperationException("Job attempt or fencing metadata is outside its supported bounds.");
        }

        if ((State == JobState.Leased) != WorkerLeaseUntilUtc.HasValue ||
            (State == JobState.Leased) != (WorkerPrincipalId is not null) ||
            (State == JobState.Leased && CurrentFencingToken < 1) ||
            (WorkerLeaseUntilUtc.HasValue &&
             (WorkerLeaseUntilUtc.Value == default || WorkerLeaseUntilUtc.Value.Offset != TimeSpan.Zero)) ||
            (State == JobState.Completed) != CompletedAtUtc.HasValue ||
            (CompletedAtUtc.HasValue &&
             (CompletedAtUtc.Value == default || CompletedAtUtc.Value.Offset != TimeSpan.Zero)) ||
            (State == JobState.Completed) != CurrentResultAttemptNumber.HasValue)
        {
            throw new InvalidOperationException("Job state and lease/result timestamps are inconsistent.");
        }

        if (WorkerPrincipalId is { Length: > JobPersistenceLimits.MaximumPrincipalIdLength } ||
            (WorkerPrincipalId is not null &&
             (string.IsNullOrWhiteSpace(WorkerPrincipalId) || WorkerPrincipalId.Any(char.IsControl))) ||
            LastFailureCode is { Length: > JobPersistenceLimits.MaximumFailureCodeLength } ||
            (LastFailureCode is not null && !JobFailureCodes.IsAllowed(LastFailureCode)) ||
            (State is JobState.RetryScheduled or JobState.Failed or JobState.DeadLettered or JobState.Stale) &&
            LastFailureCode is null ||
            (State == JobState.Completed && LastFailureCode is not null) ||
            (CurrentResultAttemptNumber is { } resultAttempt && (resultAttempt < 1 || resultAttempt > AttemptCount)))
        {
            throw new InvalidOperationException("Job audit metadata exceeds its bounded length.");
        }
    }

    public void ValidateForAuthoritativeUse(
        IJobSourceVersionDescriptorRegistry sourceValidators,
        IJobResultSchemaValidatorRegistry resultValidators) =>
        JobTypeCapabilityGate.RequireEnabled(this, sourceValidators, resultValidators);

    public static async ValueTask<JobEntity> CreatePendingPublicationAsync(
        Guid jobId,
        string organizationId,
        JobType jobType,
        Guid inputSnapshotId,
        Guid correlationId,
        int sourceVersionDescriptorSchemaVersion,
        int expectedResultSchemaVersion,
        DateTimeOffset createdAtUtc,
        int maximumAttempts,
        IJobSourceVersionDescriptorRegistry sourceValidators,
        IJobResultSchemaValidatorRegistry resultValidators,
        CancellationToken cancellationToken = default)
    {
        var descriptorSchema = JobTypeCapabilityGate.RequireEnabled(jobType, sourceVersionDescriptorSchemaVersion,
            expectedResultSchemaVersion, sourceValidators, resultValidators);
        var job = new JobEntity
        {
            JobId = jobId,
            OrganizationId = organizationId,
            JobType = jobType,
            InputSnapshotId = inputSnapshotId,
            CorrelationId = correlationId,
            EnvelopeSchemaVersion = JobEnvelope.CurrentSchemaVersion,
            SourceVersionDescriptorSchemaVersion = sourceVersionDescriptorSchemaVersion,
            ExpectedResultSchemaVersion = expectedResultSchemaVersion,
            CreatedAtUtc = createdAtUtc,
            AvailableAtUtc = createdAtUtc,
            State = JobState.PendingPublication,
            AttemptCount = 1,
            MaximumAttempts = maximumAttempts,
            CurrentFencingToken = 0
        };

        var currentFields = await descriptorSchema.CurrentVersionResolver(job, cancellationToken).ConfigureAwait(false)
            ?? throw new UnsupportedJobSourceVersionException(jobType, sourceVersionDescriptorSchemaVersion);
        var descriptor = JobSourceVersionDescriptorCodec.Encode(jobType, sourceVersionDescriptorSchemaVersion, currentFields, descriptorSchema);
        job.SourceVersionDescriptorBytes = descriptor.ExactBytes;
        job.SourceVersionHash = descriptor.Sha256;
        job.Validate();
        job.ValidateForAuthoritativeUse(sourceValidators, resultValidators);
        return job;
    }
}

/// <summary>Durable immutable envelope publication and its independent publisher lease.</summary>
public sealed class IntegrationOutboxEntity
{
    public Guid OutboxId { get; set; }
    public Guid JobId { get; set; }
    public int AttemptNumber { get; set; }
    public Guid MessageId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public int EnvelopeSchemaVersion { get; set; }
    public byte[] EnvelopeUtf8 { get; set; } = [];
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public int PublishAttemptCount { get; set; }
    public long PublisherFencingToken { get; set; }
    public string? PublisherLeaseOwner { get; set; }
    public DateTimeOffset? PublisherLeaseUntilUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public string? AcknowledgedStream { get; set; }
    public decimal? AcknowledgedSequence { get; set; }

    public void Validate()
    {
        if (OutboxId == Guid.Empty || JobId == Guid.Empty || MessageId == Guid.Empty || AttemptNumber < 1)
        {
            throw new InvalidOperationException("Outbox identifiers and attempt number are invalid.");
        }

        if (EnvelopeSchemaVersion != JobEnvelope.CurrentSchemaVersion ||
            EnvelopeUtf8.Length is 0 or > JobEnvelope.MaximumMessageBytes)
        {
            throw new InvalidOperationException("The durable envelope is outside the supported schema or size limit.");
        }

        var envelope = JobEnvelope.DeserializeFromUtf8(EnvelopeUtf8);
        if (envelope.SchemaVersion != EnvelopeSchemaVersion || envelope.MessageId != MessageId || envelope.JobId != JobId)
        {
            throw new InvalidOperationException("Outbox identifiers do not match the exact durable envelope bytes.");
        }

        if (string.IsNullOrWhiteSpace(Subject) || Subject.Length > JobPersistenceLimits.MaximumSubjectLength ||
            Subject.Any(char.IsControl) || PublishAttemptCount < 0 || PublisherFencingToken < 0 ||
            CreatedAtUtc == default || CreatedAtUtc.Offset != TimeSpan.Zero ||
            AvailableAtUtc == default || AvailableAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Outbox publication metadata is invalid.");
        }

        var leased = PublisherLeaseUntilUtc.HasValue;
        if (leased != (PublisherLeaseOwner is not null) ||
            PublisherLeaseOwner is { Length: > JobPersistenceLimits.MaximumPrincipalIdLength } ||
            (leased && PublisherFencingToken < 1) ||
            (PublisherLeaseUntilUtc.HasValue &&
             (PublisherLeaseUntilUtc.Value == default || PublisherLeaseUntilUtc.Value.Offset != TimeSpan.Zero)))
        {
            throw new InvalidOperationException("Publisher lease owner and expiry must be set together.");
        }

        var acknowledged = PublishedAtUtc.HasValue;
        if (acknowledged != (AcknowledgedStream is not null) ||
            acknowledged != AcknowledgedSequence.HasValue ||
            (AcknowledgedSequence.HasValue && (AcknowledgedSequence.Value < 1 ||
                decimal.Truncate(AcknowledgedSequence.Value) != AcknowledgedSequence.Value)) ||
            (PublishedAtUtc.HasValue &&
             (PublishedAtUtc.Value == default || PublishedAtUtc.Value.Offset != TimeSpan.Zero)) ||
            (acknowledged && leased))
        {
            throw new InvalidOperationException("PubAck details and publication state are inconsistent.");
        }
    }

    public void ValidateForJob(JobEntity job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Validate();
        job.Validate();
        var envelope = JobEnvelope.DeserializeFromUtf8(EnvelopeUtf8);
        if (JobId != job.JobId || AttemptNumber != job.AttemptCount ||
            envelope.JobType != job.JobType ||
            !string.Equals(envelope.OrganizationId, job.OrganizationId, StringComparison.Ordinal) ||
            envelope.InputSnapshotId != job.InputSnapshotId || envelope.CorrelationId != job.CorrelationId ||
            envelope.CreatedAtUtc != job.CreatedAtUtc)
        {
            throw new InvalidOperationException("The integration outbox envelope does not match its authoritative job row.");
        }
    }
}

/// <summary>Immutable redacted terminal-failure message and its independent JetStream PubAck lifecycle.</summary>
public sealed class JobDeadLetterEntity
{
    public Guid DeadLetterId { get; set; }
    public Guid JobId { get; set; }
    public int TerminalAttemptNumber { get; set; }
    public Guid MessageId { get; set; }
    public JobType JobType { get; set; }
    public string FailureCode { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public DateTimeOffset FailedAtUtc { get; set; }
    public string Subject { get; set; } = string.Empty;
    public byte[] EnvelopeUtf8 { get; set; } = [];
    public DateTimeOffset AvailableAtUtc { get; set; }
    public int PublishAttemptCount { get; set; }
    public long PublisherFencingToken { get; set; }
    public string? PublisherLeaseOwner { get; set; }
    public DateTimeOffset? PublisherLeaseUntilUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public string? AcknowledgedStream { get; set; }
    public decimal? AcknowledgedSequence { get; set; }

    public void Validate()
    {
        if (DeadLetterId == Guid.Empty || JobId == Guid.Empty || TerminalAttemptNumber < 1 ||
            MessageId == Guid.Empty || !Enum.IsDefined(JobType) ||
            JobType == SwiftAid.Contracts.Jobs.JobType.Surveillance ||
            !JobFailureCodes.IsAllowed(FailureCode) || SchemaVersion != JobDeadLetterEnvelope.CurrentSchemaVersion ||
            FailedAtUtc == default || FailedAtUtc.Offset != TimeSpan.Zero ||
            AvailableAtUtc == default || AvailableAtUtc.Offset != TimeSpan.Zero ||
            EnvelopeUtf8.Length is 0 or > JobDeadLetterEnvelope.MaximumMessageBytes ||
            PublishAttemptCount < 0 || PublisherFencingToken < 0)
        {
            throw new InvalidOperationException("Dead-letter identifiers or bounded metadata are invalid.");
        }

        var envelope = JobDeadLetterEnvelope.DeserializeFromUtf8(EnvelopeUtf8);
        if (envelope.SchemaVersion != SchemaVersion || envelope.MessageId != MessageId ||
            envelope.JobId != JobId || envelope.AttemptNumber != TerminalAttemptNumber ||
            envelope.JobType != JobType || !string.Equals(envelope.FailureCode, FailureCode, StringComparison.Ordinal) ||
            envelope.FailedAtUtc != FailedAtUtc ||
            !string.Equals(Subject, JobDeadLetterSubject.For(JobType), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The dead-letter row does not match its exact redacted envelope bytes.");
        }

        var leased = PublisherLeaseUntilUtc.HasValue;
        if (leased != (PublisherLeaseOwner is not null) ||
            PublisherLeaseOwner is { Length: > JobPersistenceLimits.MaximumPrincipalIdLength } ||
            (leased && PublisherFencingToken < 1) ||
            (PublisherLeaseUntilUtc.HasValue &&
             (PublisherLeaseUntilUtc.Value == default || PublisherLeaseUntilUtc.Value.Offset != TimeSpan.Zero)))
        {
            throw new InvalidOperationException("The dead-letter publisher lease is invalid.");
        }

        var acknowledged = PublishedAtUtc.HasValue;
        if (acknowledged != (AcknowledgedStream is not null) || acknowledged != AcknowledgedSequence.HasValue ||
            (AcknowledgedSequence.HasValue && (AcknowledgedSequence.Value < 1 ||
                decimal.Truncate(AcknowledgedSequence.Value) != AcknowledgedSequence.Value)) ||
            (PublishedAtUtc.HasValue &&
             (PublishedAtUtc.Value == default || PublishedAtUtc.Value.Offset != TimeSpan.Zero)) ||
            (acknowledged && leased) ||
            (AcknowledgedStream is { Length: > JobPersistenceLimits.MaximumSubjectLength }))
        {
            throw new InvalidOperationException("The dead-letter PubAck state is invalid.");
        }
    }
}

/// <summary>One durable worker claim, keyed by logical job and attempt for duplicate-message safety.</summary>
public sealed class WorkerInboxEntity
{
    public Guid JobId { get; set; }
    public int AttemptNumber { get; set; }
    public Guid MessageId { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public WorkerAttemptState State { get; set; }
    public long FencingToken { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public string? WorkerPrincipalId { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public DateTimeOffset? RetryAtUtc { get; set; }
    public bool IsAuthorizedReplay { get; set; }
    public string? ReplayActorId { get; set; }
    public string? ReplayReasonCode { get; set; }

    public static WorkerInboxEntity CreatePending(
        JobEntity job,
        IntegrationOutboxEntity publishedOutbox,
        DateTimeOffset receivedAtUtc,
        bool isAuthorizedReplay = false,
        string? replayActorId = null,
        string? replayReasonCode = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(publishedOutbox);
        job.Validate();
        publishedOutbox.ValidateForJob(job);
        if (job.State != JobState.Published || !publishedOutbox.PublishedAtUtc.HasValue ||
            publishedOutbox.AttemptNumber != job.AttemptCount || publishedOutbox.JobId != job.JobId)
        {
            throw new InvalidOperationException("A pending inbox attempt requires a matching PubAcked job outbox row.");
        }

        var inbox = new WorkerInboxEntity
        {
            JobId = job.JobId,
            AttemptNumber = publishedOutbox.AttemptNumber,
            MessageId = publishedOutbox.MessageId,
            ReceivedAtUtc = receivedAtUtc,
            State = WorkerAttemptState.Pending,
            IsAuthorizedReplay = isAuthorizedReplay,
            ReplayActorId = replayActorId,
            ReplayReasonCode = replayReasonCode
        };
        inbox.Validate();
        return inbox;
    }

    public void Validate()
    {
        if (JobId == Guid.Empty || AttemptNumber < 1 || MessageId == Guid.Empty || FencingToken < 0 ||
            ReceivedAtUtc == default || ReceivedAtUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(State))
        {
            throw new InvalidOperationException("Worker inbox identity, state, or fencing metadata is invalid.");
        }

        var activeLease = State == WorkerAttemptState.Leased;
        if (activeLease != LeaseUntilUtc.HasValue ||
            (State == WorkerAttemptState.Pending && (FencingToken != 0 || WorkerPrincipalId is not null)) ||
            (activeLease && (FencingToken < 1 || string.IsNullOrWhiteSpace(WorkerPrincipalId))) ||
            (LeaseUntilUtc.HasValue &&
             (LeaseUntilUtc.Value == default || LeaseUntilUtc.Value.Offset != TimeSpan.Zero)) ||
            (State is WorkerAttemptState.Completed or WorkerAttemptState.Failed or WorkerAttemptState.Stale or
                WorkerAttemptState.DeadLettered or WorkerAttemptState.Cancelled) != FinishedAtUtc.HasValue ||
            (FinishedAtUtc.HasValue &&
             (FinishedAtUtc.Value == default || FinishedAtUtc.Value.Offset != TimeSpan.Zero)) ||
            (State == WorkerAttemptState.RetryScheduled) != RetryAtUtc.HasValue ||
            (RetryAtUtc.HasValue &&
             (RetryAtUtc.Value == default || RetryAtUtc.Value.Offset != TimeSpan.Zero)))
        {
            throw new InvalidOperationException("Worker attempt state and timestamps are inconsistent.");
        }

        if ((WorkerPrincipalId is not null &&
             (string.IsNullOrWhiteSpace(WorkerPrincipalId) ||
              WorkerPrincipalId.Length > JobPersistenceLimits.MaximumPrincipalIdLength ||
              WorkerPrincipalId.Any(char.IsControl))) ||
            FailureCode is { Length: > JobPersistenceLimits.MaximumFailureCodeLength } ||
            (FailureCode is not null && !JobFailureCodes.IsAllowed(FailureCode)) ||
            (State is WorkerAttemptState.RetryScheduled or WorkerAttemptState.Failed or
                WorkerAttemptState.Stale or WorkerAttemptState.DeadLettered) && FailureCode is null ||
            (State == WorkerAttemptState.Completed && FailureCode is not null) ||
            ReplayActorId is { Length: > JobPersistenceLimits.MaximumPrincipalIdLength } ||
            ReplayReasonCode is { Length: > JobPersistenceLimits.MaximumReasonCodeLength } ||
            IsAuthorizedReplay != (ReplayActorId is not null && ReplayReasonCode is not null) ||
            (!IsAuthorizedReplay && (ReplayActorId is not null || ReplayReasonCode is not null)))
        {
            throw new InvalidOperationException("Worker audit and replay metadata is invalid or unbounded.");
        }
    }
}

/// <summary>Immutable, schema-validated result bytes accepted for one fenced attempt.</summary>
public sealed class JobResultEntity
{
    public Guid JobId { get; set; }
    public int AttemptNumber { get; set; }
    public int ResultSchemaVersion { get; set; }
    public byte[] SourceVersionHash { get; set; } = [];
    public byte[] PayloadUtf8 { get; set; } = [];
    public byte[] PayloadSha256 { get; set; } = [];
    public long FencingToken { get; set; }
    public string WorkerPrincipalId { get; set; } = string.Empty;
    public DateTimeOffset AcceptedAtUtc { get; set; }

    public void Validate(int maximumPayloadBytes = JobPersistenceLimits.DefaultMaximumResultBytes)
    {
        if (maximumPayloadBytes is < 1 or > JobPersistenceLimits.DefaultMaximumResultBytes ||
            JobId == Guid.Empty || AttemptNumber < 1 || ResultSchemaVersion < 1 || FencingToken < 1 ||
            SourceVersionHash.Length != JobSourceVersionHash.HashLength ||
            PayloadSha256.Length != JobSourceVersionHash.HashLength ||
            PayloadUtf8.Length is 0 || PayloadUtf8.Length > maximumPayloadBytes ||
            string.IsNullOrWhiteSpace(WorkerPrincipalId) ||
            WorkerPrincipalId.Length > JobPersistenceLimits.MaximumPrincipalIdLength ||
            AcceptedAtUtc == default || AcceptedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The accepted job result metadata or bounded payload is invalid.");
        }

        if (!CryptographicOperations.FixedTimeEquals(PayloadSha256, SHA256.HashData(PayloadUtf8)))
        {
            throw new InvalidOperationException("The result hash does not match its exact accepted UTF-8 bytes.");
        }
    }
}

/// <summary>Metadata-only audit for a response rejected as stale or otherwise not authoritative.</summary>
public sealed class JobResultAuditEntity
{
    public Guid AuditId { get; set; }
    public Guid JobId { get; set; }
    public int AttemptNumber { get; set; }
    public long FencingToken { get; set; }
    public byte[] SourceVersionHash { get; set; } = [];
    public byte[] CurrentSourceVersionHash { get; set; } = [];
    public byte[] ResultHash { get; set; } = [];
    public string DecisionCode { get; set; } = string.Empty;
    public string WorkerPrincipalId { get; set; } = string.Empty;
    public DateTimeOffset RecordedAtUtc { get; set; }

    public void Validate()
    {
        if (AuditId == Guid.Empty || JobId == Guid.Empty || AttemptNumber < 1 || FencingToken < 0 ||
            SourceVersionHash.Length != JobSourceVersionHash.HashLength ||
            CurrentSourceVersionHash.Length != JobSourceVersionHash.HashLength ||
            ResultHash.Length != JobSourceVersionHash.HashLength ||
            string.IsNullOrWhiteSpace(DecisionCode) || !JobResultAuditDecisionCodes.IsAllowed(DecisionCode) ||
            DecisionCode.Length > JobPersistenceLimits.MaximumFailureCodeLength ||
            string.IsNullOrWhiteSpace(WorkerPrincipalId) ||
            WorkerPrincipalId.Length > JobPersistenceLimits.MaximumPrincipalIdLength ||
            RecordedAtUtc == default || RecordedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The bounded job-result audit metadata is invalid.");
        }
    }
}

public enum JobState
{
    PendingPublication,
    Published,
    Leased,
    RetryScheduled,
    Completed,
    Failed,
    DeadLettered,
    Cancelled,
    Stale
}

public enum WorkerAttemptState
{
    Pending,
    Leased,
    RetryScheduled,
    Completed,
    Failed,
    Stale,
    DeadLettered,
    Cancelled
}

/// <summary>Fixed bounds shared by entity validation and the later persistence mapping.</summary>
public static class JobPersistenceLimits
{
    public const int MaximumAttempts = 5;
    public const int MaximumOrganizationIdLength = 200;
    public const int MaximumSubjectLength = 200;
    public const int MaximumPrincipalIdLength = 256;
    public const int MaximumFailureCodeLength = 64;
    public const int MaximumReasonCodeLength = 128;
    public const int DefaultMaximumResultBytes = 1024 * 1024;
}
