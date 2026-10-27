using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftAid.Contracts.Jobs;

namespace SwiftAid.Infrastructure.Persistence.Jobs;

/// <summary>Redacted, bounded terminal-job metadata sent to the separate JetStream DLQ.</summary>
public sealed record JobDeadLetterEnvelope(
    int SchemaVersion,
    Guid MessageId,
    Guid JobId,
    JobType JobType,
    int AttemptNumber,
    string FailureCode,
    DateTimeOffset FailedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumMessageBytes = 64 * 1024;
    public const string StreamName = "SWIFTAID_DLQ";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public JobDeadLetterEnvelope Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || MessageId == Guid.Empty || JobId == Guid.Empty ||
            AttemptNumber < 1 || !Enum.IsDefined(JobType) ||
            JobType == SwiftAid.Contracts.Jobs.JobType.Surveillance ||
            !JobFailureCodes.IsAllowed(FailureCode) || FailedAtUtc == default || FailedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The dead-letter envelope metadata is invalid or disabled.");
        }

        return this;
    }

    public byte[] SerializeToUtf8()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, SerializerOptions);
        if (bytes.Length is 0 or > MaximumMessageBytes)
        {
            throw new ArgumentException("The dead-letter envelope exceeds its 64 KiB limit.");
        }

        return bytes;
    }

    public static JobDeadLetterEnvelope DeserializeFromUtf8(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumMessageBytes)
        {
            throw new ArgumentException("A dead-letter envelope must be between 1 byte and 64 KiB.", nameof(bytes));
        }

        using (var document = JsonDocument.Parse(bytes.ToArray()))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The dead-letter envelope must be a JSON object.");
            }

            var requiredProperties = new HashSet<string>(StringComparer.Ordinal)
            {
                "schemaVersion",
                "messageId",
                "jobId",
                "jobType",
                "attemptNumber",
                "failureCode",
                "failedAtUtc"
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!requiredProperties.Contains(property.Name) || !seen.Add(property.Name))
                {
                    throw new JsonException("The dead-letter envelope contains an unknown or duplicate field.");
                }
            }

            if (seen.Count != requiredProperties.Count)
            {
                throw new JsonException("The dead-letter envelope is missing a required field.");
            }

            var typeElement = document.RootElement.GetProperty("jobType");
            var typeName = typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : null;
            if (typeName is null || !Enum.GetNames<JobType>().Contains(typeName, StringComparer.Ordinal))
            {
                throw new JsonException("The dead-letter job type is unknown or non-canonical.");
            }
        }

        var envelope = JsonSerializer.Deserialize<JobDeadLetterEnvelope>(bytes, SerializerOptions)
            ?? throw new JsonException("The dead-letter envelope cannot be null.");
        return envelope.Validate();
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter<JobType>(allowIntegerValues: false));
        return options;
    }
}

/// <summary>Maps only enabled, closed job types to their durable metadata-only dead-letter subject.</summary>
public static class JobDeadLetterSubject
{
    public static string For(JobType jobType) => jobType switch
    {
        JobType.Report => "swiftaid.dlq.report",
        JobType.CaseSummary => "swiftaid.dlq.case-summary",
        JobType.OpsContinuity => "swiftaid.dlq.ops-continuity",
        JobType.Knowledge => "swiftaid.dlq.knowledge",
        JobType.Reminder => "swiftaid.dlq.reminder",
        JobType.MemoryIndex => "swiftaid.dlq.memory-index",
        JobType.Surveillance => throw new UnsupportedDeadLetterJobTypeException(jobType),
        _ => throw new UnsupportedDeadLetterJobTypeException(jobType)
    };
}

public sealed class UnsupportedDeadLetterJobTypeException(JobType jobType)
    : InvalidOperationException($"Job type '{jobType}' is not enabled for dead-letter dispatch.");
