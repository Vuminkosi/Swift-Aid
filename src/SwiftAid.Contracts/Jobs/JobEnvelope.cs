using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwiftAid.Contracts.Jobs;

/// <summary>The fixed, opaque worker job categories supported by the version-one queue contract.</summary>
public enum JobType
{
    Report,
    CaseSummary,
    OpsContinuity,
    Surveillance,
    Knowledge,
    Reminder,
    MemoryIndex
}

/// <summary>
/// An opaque reference to durable job state. Workers must resolve job input through the gateway;
/// this envelope must never contain case evidence, source text, credentials, or media.
/// </summary>
public sealed record JobEnvelope(
    int SchemaVersion,
    Guid MessageId,
    Guid JobId,
    JobType JobType,
    string OrganizationId,
    Guid InputSnapshotId,
    Guid CorrelationId,
    DateTimeOffset CreatedAtUtc)
{
    public const int CurrentSchemaVersion = 1;

    public const int MaximumMessageBytes = 64 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public JobEnvelope Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(SchemaVersion), "The job envelope schema version is unsupported.");
        }

        if (MessageId == Guid.Empty || JobId == Guid.Empty || InputSnapshotId == Guid.Empty || CorrelationId == Guid.Empty)
        {
            throw new ArgumentException("All job envelope identifiers must be non-empty.");
        }

        if (!Enum.IsDefined(JobType))
        {
            throw new ArgumentOutOfRangeException(nameof(JobType), "The job type is unsupported.");
        }

        if (string.IsNullOrWhiteSpace(OrganizationId) ||
            !string.Equals(OrganizationId, OrganizationId.Trim(), StringComparison.Ordinal) ||
            OrganizationId.Length > 200 ||
            OrganizationId.Any(char.IsControl))
        {
            throw new ArgumentException("A canonical opaque organization identifier is required.", nameof(OrganizationId));
        }

        if (CreatedAtUtc == default || CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The creation timestamp must be a non-default UTC instant.", nameof(CreatedAtUtc));
        }

        return this;
    }

    public byte[] SerializeToUtf8()
    {
        Validate();
        var payload = JsonSerializer.SerializeToUtf8Bytes(this, SerializerOptions);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new ArgumentException("The serialized job envelope exceeds the 64 KiB message limit.");
        }

        return payload;
    }

    public static JobEnvelope DeserializeFromUtf8(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > MaximumMessageBytes)
        {
            throw new ArgumentException("The job envelope payload must be between 1 byte and 64 KiB.", nameof(payload));
        }

        using (var document = JsonDocument.Parse(payload.ToArray()))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The job envelope must be a JSON object.");
            }

            var expectedProperties = new HashSet<string>(StringComparer.Ordinal)
            {
                "schemaVersion",
                "messageId",
                "jobId",
                "jobType",
                "organizationId",
                "inputSnapshotId",
                "correlationId",
                "createdAtUtc"
            };
            var seenProperties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!expectedProperties.Contains(property.Name) || !seenProperties.Add(property.Name))
                {
                    throw new JsonException("The job envelope contains an unknown or duplicate field.");
                }
            }

            if (seenProperties.Count != expectedProperties.Count)
            {
                throw new JsonException("The job envelope is missing a required field.");
            }

            var jobTypeValue = document.RootElement.GetProperty("jobType");
            var jobTypeName = jobTypeValue.ValueKind == JsonValueKind.String ? jobTypeValue.GetString() : null;
            if (jobTypeName is null || !Enum.GetNames<JobType>().Contains(jobTypeName, StringComparer.Ordinal))
            {
                throw new JsonException("The job envelope job type is unknown or non-canonical.");
            }
        }

        var envelope = JsonSerializer.Deserialize<JobEnvelope>(payload, SerializerOptions)
            ?? throw new JsonException("The job envelope payload cannot be null.");

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
