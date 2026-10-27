using System.Security.Cryptography;
using System.Text;
using SwiftAid.Contracts.Jobs;
using SwiftAid.Infrastructure.Persistence.Jobs;

namespace SwiftAid.Integration.Tests;

public sealed class JobSourceVersionDescriptorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EncoderUsesExactGoldenVectorAndOrdinalFieldOrdering()
    {
        var schema = CreateSchema([
            new("b", JobSourceVersionFieldType.Int64, true),
            new("a", JobSourceVersionFieldType.Boolean, true)
        ]);

        var first = JobSourceVersionDescriptorCodec.Encode(JobType.Report, 1,
            [JobSourceVersionFieldValue.Int64("b", 1), JobSourceVersionFieldValue.Boolean("a", true)], schema);
        var second = JobSourceVersionDescriptorCodec.Encode(JobType.Report, 1,
            [JobSourceVersionFieldValue.Boolean("a", true), JobSourceVersionFieldValue.Int64("b", 1)], schema);

        Assert.Equal("53574946544149442D535631000100065265706F7274000200016102000000010100016203000000080000000000000001",
            Convert.ToHexString(first.ExactBytes));
        Assert.Equal(first.ExactBytes, second.ExactBytes);
        Assert.Equal(SHA256.HashData(first.ExactBytes), first.Sha256);
        Assert.Equal(first.Sha256, second.Sha256);
    }

    [Fact]
    public void OmittedOptionalFieldAndExplicitNullHaveDifferentCanonicalBytes()
    {
        var schema = CreateSchema([new("optional", JobSourceVersionFieldType.Utf8Nfc, false, AllowsNull: true)]);

        var missing = JobSourceVersionDescriptorCodec.Encode(JobType.Report, 1, [], schema);
        var explicitNull = JobSourceVersionDescriptorCodec.Encode(JobType.Report, 1,
            [JobSourceVersionFieldValue.Null("optional")], schema);

        Assert.NotEqual(missing.ExactBytes, explicitNull.ExactBytes);
        Assert.NotEqual(missing.Sha256, explicitNull.Sha256);
        Assert.Equal((byte)JobSourceVersionFieldType.Null, explicitNull.ExactBytes[^5]);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, explicitNull.ExactBytes[^4..]);
    }

    [Fact]
    public void CodecRejectsDuplicateUnknownMissingAndMalformedValues()
    {
        var schema = CreateSchema([
            new("required", JobSourceVersionFieldType.Boolean, true),
            new("choice", JobSourceVersionFieldType.ClosedEnumUtf8, false, new HashSet<string>(StringComparer.Ordinal) { "green", "blue" })
        ]);

        Assert.Throws<ArgumentException>(() => JobSourceVersionDescriptorCodec.Encode(JobType.Report, 1, [], schema));
        Assert.Throws<ArgumentException>(() => JobSourceVersionDescriptorCodec.Encode(JobType.Report, 1,
            [JobSourceVersionFieldValue.Boolean("required", true), JobSourceVersionFieldValue.Boolean("required", false)], schema));
        Assert.Throws<ArgumentException>(() => JobSourceVersionDescriptorCodec.Encode(JobType.Report, 1,
            [JobSourceVersionFieldValue.Boolean("required", true), JobSourceVersionFieldValue.Int64("unknown", 1)], schema));
        Assert.Throws<ArgumentException>(() => JobSourceVersionDescriptorCodec.Encode(JobType.Report, 1,
            [new("required", JobSourceVersionFieldType.Boolean, [2])], schema));
        Assert.Throws<ArgumentException>(() => JobSourceVersionDescriptorCodec.Encode(JobType.Report, 1,
            [JobSourceVersionFieldValue.Boolean("required", true), JobSourceVersionFieldValue.ClosedEnum("choice", "red")], schema));
        Assert.Throws<ArgumentException>(() => JobSourceVersionDescriptorCodec.Encode(JobType.Report, 1,
            [JobSourceVersionFieldValue.Boolean("required", true), new("choice", JobSourceVersionFieldType.ClosedEnumUtf8, [0xC3, 0x28])], schema));
    }

    [Fact]
    public void ScalarFactoriesUseNetworkUuidAndBigEndianIntegerAndUtcTicks()
    {
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), JobSourceVersionFieldValue.Uuid("uuid", id).ValueBytes);
        Assert.Equal(Convert.FromHexString("0102030405060708"), JobSourceVersionFieldValue.Int64("number", 0x0102030405060708).ValueBytes);
        Assert.Equal(Convert.FromHexString("08DF34004C9E8000"),
            JobSourceVersionFieldValue.UtcDateTime("when", new DateTime(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc)).ValueBytes);
        Assert.Throws<ArgumentException>(() => JobSourceVersionFieldValue.UtcDateTime("when", DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Local)));
    }

    [Fact]
    public void CodecRejectsNoncanonicalOrderAndHashTampering()
    {
        var schema = CreateSchema([
            new("a", JobSourceVersionFieldType.Boolean, true),
            new("b", JobSourceVersionFieldType.Boolean, true)
        ]);
        var encoded = JobSourceVersionDescriptorCodec.Encode(JobType.Report, 1,
            [JobSourceVersionFieldValue.Boolean("a", true), JobSourceVersionFieldValue.Boolean("b", false)], schema);
        var reversed = JobSourceVersionDescriptorCodec.Encode(JobType.Report, 1,
            [JobSourceVersionFieldValue.Boolean("b", false), JobSourceVersionFieldValue.Boolean("a", true)], schema);
        var tamperedHash = encoded.Sha256.ToArray();
        tamperedHash[0] ^= 0xFF;

        JobSourceVersionDescriptorCodec.ValidateStored(JobType.Report, 1, encoded.ExactBytes, encoded.Sha256, schema);
        Assert.Throws<ArgumentException>(() => JobSourceVersionDescriptorCodec.ValidateStored(JobType.Report, 1, reversed.ExactBytes, tamperedHash, schema));
    }

    [Fact]
    public async Task MissingDescriptorOrResultCapabilityFailsClosedAndSurveillanceCannotBeRegistered()
    {
        Assert.Throws<UnsupportedJobSourceVersionException>(() => JobSourceVersionDescriptorResolver.Require(
            new DescriptorRegistry(), JobType.Report, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSchema([], JobType.Surveillance));

        var schema = CreateSchema([new("v", JobSourceVersionFieldType.Int64, true)]);
        var sourceRegistry = new DescriptorRegistry(schema);
        var noResults = new ResultRegistry();
        await Assert.ThrowsAsync<UnsupportedJobResultSchemaException>(async () =>
            await JobEntity.CreatePendingPublicationAsync(Guid.NewGuid(), "org-x", JobType.Report,
                Guid.NewGuid(), Guid.NewGuid(), 1, 1, Now, 2, sourceRegistry, noResults));
    }

    [Fact]
    public async Task TryClaimCreatesPendingAttemptAndOnlyReturnsFenceForGrantedClaim()
    {
        var (job, outbox, sourceRegistry, resultRegistry, currentHash) = await CreatePublishedJob();
        var transition = JobPersistenceTransitions.TryClaim(job, outbox, null, "worker-a", Now.AddSeconds(1),
            Now.AddMinutes(1), currentHash, sourceRegistry, resultRegistry);

        Assert.Equal(JobClaimDisposition.Granted, transition.Disposition);
        Assert.True(transition.FencingToken > 0);
        Assert.Equal(WorkerAttemptState.Leased, transition.Attempt!.State);
        Assert.Equal(transition.FencingToken!.Value, transition.Attempt.FencingToken);
        Assert.Equal(Now.AddMinutes(1), transition.Attempt.LeaseUntilUtc);

        var duplicate = JobPersistenceTransitions.ResolveDuplicateClaim(job, transition.Attempt, Now.AddSeconds(2));
        Assert.Equal(JobClaimDisposition.AlreadyLeased, duplicate.Disposition);
        Assert.Null(duplicate.FencingToken);
        Assert.Null(duplicate.LeaseUntilUtc);
    }

    [Fact]
    public async Task StaleCompletionReturnsOnlyHashAuditAndDoesNotAcceptPayload()
    {
        var (job, outbox, sourceRegistry, resultRegistry, currentHash) = await CreatePublishedJob();
        var claim = JobPersistenceTransitions.TryClaim(job, outbox, null, "worker-a", Now.AddSeconds(1),
            Now.AddMinutes(1), currentHash, sourceRegistry, resultRegistry);
        var bytes = Encoding.UTF8.GetBytes("{\"value\":\"private-result\"}");
        var result = new JobResultEntity
        {
            JobId = job.JobId,
            AttemptNumber = 1,
            ResultSchemaVersion = 1,
            SourceVersionHash = job.SourceVersionHash.ToArray(),
            PayloadUtf8 = bytes,
            PayloadSha256 = SHA256.HashData(bytes),
            FencingToken = claim.FencingToken!.Value,
            WorkerPrincipalId = "worker-a",
            AcceptedAtUtc = Now.AddSeconds(2)
        };
        var changedSourceHash = SHA256.HashData("new-source"u8);

        result.WorkerPrincipalId = "worker-b";
        Assert.Throws<ArgumentException>(() => JobPersistenceTransitions.Complete(job, claim.Attempt!, result, null,
            "worker-a", claim.FencingToken.Value, changedSourceHash, Now.AddSeconds(2), sourceRegistry, resultRegistry));
        Assert.Equal(JobState.Leased, job.State);
        result.WorkerPrincipalId = "worker-a";

        var completion = JobPersistenceTransitions.Complete(job, claim.Attempt!, result, null, "worker-a",
            claim.FencingToken.Value, changedSourceHash, Now.AddSeconds(2), sourceRegistry, resultRegistry);

        Assert.Equal(JobCompletionDisposition.StaleSourceVersion, completion.Disposition);
        Assert.NotNull(completion.Audit);
        Assert.Equal(job.SourceVersionHash, completion.Audit.SourceVersionHash);
        Assert.Equal(changedSourceHash, completion.Audit.CurrentSourceVersionHash);
        Assert.Equal(result.PayloadSha256, completion.Audit.ResultHash);
        Assert.Equal(JobState.Stale, job.State);
        Assert.DoesNotContain("private-result", System.Text.Json.JsonSerializer.Serialize(completion.Audit));
    }

    [Fact]
    public async Task StaleFencingCompletionReturnsHashOnlyAuditWithoutChangingJobOrReturningPayload()
    {
        var (job, outbox, sourceRegistry, resultRegistry, currentHash) = await CreatePublishedJob();
        var claim = JobPersistenceTransitions.TryClaim(job, outbox, null, "worker-a", Now.AddSeconds(1),
            Now.AddMinutes(1), currentHash, sourceRegistry, resultRegistry);
        var bytes = Encoding.UTF8.GetBytes("{\"value\":\"private-fenced-result\"}");
        var result = new JobResultEntity
        {
            JobId = job.JobId, AttemptNumber = 1, ResultSchemaVersion = 1,
            SourceVersionHash = job.SourceVersionHash.ToArray(), PayloadUtf8 = bytes,
            PayloadSha256 = SHA256.HashData(bytes), FencingToken = claim.FencingToken!.Value,
            WorkerPrincipalId = "worker-a", AcceptedAtUtc = Now.AddSeconds(2)
        };

        var completion = JobPersistenceTransitions.Complete(job, claim.Attempt!, result, null, "worker-a",
            claim.FencingToken.Value + 1, currentHash, Now.AddSeconds(2), sourceRegistry, resultRegistry);

        Assert.Equal(JobCompletionDisposition.StaleFencingToken, completion.Disposition);
        Assert.NotNull(completion.Audit);
        Assert.Equal(JobResultAuditDecisionCodes.StaleFencingToken, completion.Audit.DecisionCode);
        Assert.Equal(SHA256.HashData(bytes), completion.Audit.ResultHash);
        Assert.Equal(JobState.Leased, job.State);
        Assert.DoesNotContain("private-fenced-result", System.Text.Json.JsonSerializer.Serialize(completion));
    }

    [Fact]
    public async Task DuplicateResultHashMismatchReturnsMetadataAuditWithoutReplacingAcceptedResult()
    {
        var (job, outbox, sourceRegistry, resultRegistry, currentHash) = await CreatePublishedJob();
        var claim = JobPersistenceTransitions.TryClaim(job, outbox, null, "worker-a", Now.AddSeconds(1),
            Now.AddMinutes(1), currentHash, sourceRegistry, resultRegistry);
        var acceptedBytes = Encoding.UTF8.GetBytes("{\"value\":\"accepted\"}");
        var accepted = new JobResultEntity
        {
            JobId = job.JobId, AttemptNumber = 1, ResultSchemaVersion = 1,
            SourceVersionHash = job.SourceVersionHash.ToArray(), PayloadUtf8 = acceptedBytes,
            PayloadSha256 = SHA256.HashData(acceptedBytes), FencingToken = claim.FencingToken!.Value,
            WorkerPrincipalId = "worker-a", AcceptedAtUtc = Now.AddSeconds(2)
        };
        var acceptedTransition = JobPersistenceTransitions.Complete(job, claim.Attempt!, accepted, null,
            "worker-a", claim.FencingToken.Value, currentHash, Now.AddSeconds(2), sourceRegistry, resultRegistry);
        Assert.Equal(JobCompletionDisposition.Accepted, acceptedTransition.Disposition);
        var committedHash = accepted.PayloadSha256.ToArray();

        var duplicateBytes = Encoding.UTF8.GetBytes("{\"value\":\"private-replacement\"}");
        var duplicate = new JobResultEntity
        {
            JobId = job.JobId, AttemptNumber = 1, ResultSchemaVersion = 1,
            SourceVersionHash = job.SourceVersionHash.ToArray(), PayloadUtf8 = duplicateBytes,
            PayloadSha256 = SHA256.HashData(duplicateBytes), FencingToken = claim.FencingToken.Value,
            WorkerPrincipalId = "worker-a", AcceptedAtUtc = Now.AddSeconds(3)
        };

        var completion = JobPersistenceTransitions.Complete(job, claim.Attempt!, duplicate, accepted,
            "worker-a", claim.FencingToken.Value, currentHash, Now.AddSeconds(3), sourceRegistry, resultRegistry);

        Assert.Equal(JobCompletionDisposition.DuplicateResultMismatch, completion.Disposition);
        Assert.NotNull(completion.Audit);
        Assert.Equal(JobResultAuditDecisionCodes.DuplicateHashMismatch, completion.Audit.DecisionCode);
        Assert.Equal(SHA256.HashData(duplicateBytes), completion.Audit.ResultHash);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(committedHash, accepted.PayloadSha256);
        Assert.DoesNotContain("private-replacement", System.Text.Json.JsonSerializer.Serialize(completion));
    }

    [Fact]
    public void DeadLetterEnvelopeRejectsPrivateFieldsAndAcceptsOnlyExactSevenFieldShape()
    {
        var envelope = new JobDeadLetterEnvelope(1, Guid.NewGuid(), Guid.NewGuid(), JobType.Report, 3,
            JobFailureCodes.ServiceUnavailable, Now);
        var bytes = envelope.SerializeToUtf8();
        Assert.Equal(envelope, JobDeadLetterEnvelope.DeserializeFromUtf8(bytes));

        using var document = System.Text.Json.JsonDocument.Parse(bytes);
        Assert.Equal(7, document.RootElement.EnumerateObject().Count());
        var withPrivateData = Encoding.UTF8.GetString(bytes).Replace("}", ",\"caseId\":\"private\"}", StringComparison.Ordinal);
        Assert.Throws<System.Text.Json.JsonException>(() => JobDeadLetterEnvelope.DeserializeFromUtf8(Encoding.UTF8.GetBytes(withPrivateData)));
        Assert.Throws<UnsupportedDeadLetterJobTypeException>(() => JobDeadLetterSubject.For(JobType.Surveillance));
    }

    private static JobSourceVersionDescriptorSchema CreateSchema(
        IEnumerable<JobSourceVersionFieldDefinition> fields,
        JobType type = JobType.Report) => new(type, 1, fields,
            (_, _) => ValueTask.FromResult<IReadOnlyCollection<JobSourceVersionFieldValue>?>(
                [JobSourceVersionFieldValue.Int64("source-version", 1)]));

    private static async Task<(JobEntity Job, IntegrationOutboxEntity Outbox, DescriptorRegistry Sources, ResultRegistry Results, byte[] Hash)> CreatePublishedJob()
    {
        var schema = CreateSchema([new("source-version", JobSourceVersionFieldType.Int64, true)]);
        var sources = new DescriptorRegistry(schema);
        var results = new ResultRegistry(new AnyObjectValidator());
        var jobId = Guid.NewGuid();
        var org = "org-x";
        var snapshot = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var envelopeMessageId = Guid.NewGuid();
        var job = await JobEntity.CreatePendingPublicationAsync(jobId, org, JobType.Report,
            snapshot, correlation, 1, 1, Now, 2, sources, results);
        var envelope = new JobEnvelope(1, envelopeMessageId, jobId, JobType.Report, org, snapshot, correlation, Now);
        var outbox = new IntegrationOutboxEntity
        {
            OutboxId = Guid.NewGuid(), JobId = jobId, AttemptNumber = 1, MessageId = envelopeMessageId,
            Subject = "swiftaid.jobs.report", EnvelopeSchemaVersion = 1, EnvelopeUtf8 = envelope.SerializeToUtf8(),
            CreatedAtUtc = Now, AvailableAtUtc = Now, PublishedAtUtc = Now,
            AcknowledgedStream = "SWIFTAID_JOBS", AcknowledgedSequence = 10
        };
        job.State = JobState.Published;
        return (job, outbox, sources, results, job.SourceVersionHash.ToArray());
    }

    private sealed class DescriptorRegistry(params JobSourceVersionDescriptorSchema[] schemas) : IJobSourceVersionDescriptorRegistry
    {
        public JobSourceVersionDescriptorSchema? Find(JobType jobType, int descriptorSchemaVersion) =>
            schemas.SingleOrDefault(schema => schema.JobType == jobType && schema.SchemaVersion == descriptorSchemaVersion);
    }

    private sealed class ResultRegistry(params IJobResultSchemaValidator[] validators) : IJobResultSchemaValidatorRegistry
    {
        public IJobResultSchemaValidator? Find(JobType jobType, int schemaVersion) =>
            validators.SingleOrDefault(validator => validator.JobType == jobType && validator.SchemaVersion == schemaVersion);
    }

    private sealed class AnyObjectValidator : IJobResultSchemaValidator
    {
        public JobType JobType => JobType.Report;
        public int SchemaVersion => 1;
        public void Validate(ReadOnlyMemory<byte> jsonUtf8) { }
    }
}
