using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SwiftAid.Contracts.Jobs;

namespace SwiftAid.Infrastructure.Persistence.Jobs;

/// <summary>Configurable limits for opaque job result payloads; deployments may lower the default only.</summary>
public sealed class JobPersistenceOptions
{
    public const string SectionName = "Jobs:Persistence";

    public int MaximumResultBytes { get; set; } = JobPersistenceLimits.DefaultMaximumResultBytes;

    public void Validate()
    {
        if (MaximumResultBytes is < 1 or > JobPersistenceLimits.DefaultMaximumResultBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumResultBytes),
                "The job result limit must be positive and may not exceed 1 MiB.");
        }
    }
}

/// <summary>Primitive field encodings in the immutable source-version descriptor v1 format.</summary>
public enum JobSourceVersionFieldType : byte
{
    Uuid = 1,
    Boolean = 2,
    Int64 = 3,
    UtcDateTimeTicks = 4,
    Sha256 = 5,
    ClosedEnumUtf8 = 6,
    Utf8Nfc = 7,
    Null = 255
}

public sealed record JobSourceVersionFieldDefinition(
    string FieldId,
    JobSourceVersionFieldType Type,
    bool Required,
    IReadOnlySet<string>? AllowedValues = null,
    bool AllowsNull = false);

/// <summary>A typed descriptor value. Byte-valued factories defensively copy caller data.</summary>
public sealed record JobSourceVersionFieldValue(string FieldId, JobSourceVersionFieldType Type, byte[] ValueBytes)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static JobSourceVersionFieldValue Uuid(string fieldId, Guid value)
    {
        var bytes = Convert.FromHexString(value.ToString("N"));
        // RFC 4122/network order is the canonical textual UUID order, unlike Guid.ToByteArray().
        return new(fieldId, JobSourceVersionFieldType.Uuid, bytes);
    }

    public static JobSourceVersionFieldValue Boolean(string fieldId, bool value) =>
        new(fieldId, JobSourceVersionFieldType.Boolean, [value ? (byte)1 : (byte)0]);

    public static JobSourceVersionFieldValue Int64(string fieldId, long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return new(fieldId, JobSourceVersionFieldType.Int64, bytes);
    }

    public static JobSourceVersionFieldValue UtcDateTime(string fieldId, DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Source-version date values must be UTC.", nameof(value));
        }

        return Int64(fieldId, value.Ticks) with { Type = JobSourceVersionFieldType.UtcDateTimeTicks };
    }

    public static JobSourceVersionFieldValue Sha256(string fieldId, ReadOnlySpan<byte> value)
    {
        if (value.Length != JobSourceVersionHash.HashLength)
        {
            throw new ArgumentException("A SHA-256 field must contain exactly 32 bytes.", nameof(value));
        }

        return new(fieldId, JobSourceVersionFieldType.Sha256, value.ToArray());
    }

    public static JobSourceVersionFieldValue ClosedEnum(string fieldId, string value) =>
        Text(fieldId, JobSourceVersionFieldType.ClosedEnumUtf8, value);

    public static JobSourceVersionFieldValue Utf8Nfc(string fieldId, string value) =>
        Text(fieldId, JobSourceVersionFieldType.Utf8Nfc, value);

    public static JobSourceVersionFieldValue Null(string fieldId) =>
        new(fieldId, JobSourceVersionFieldType.Null, []);

    private static JobSourceVersionFieldValue Text(string fieldId, JobSourceVersionFieldType type, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsNormalized(NormalizationForm.FormC))
        {
            throw new ArgumentException("Descriptor text must already be NFC-normalized.", nameof(value));
        }

        return new(fieldId, type, StrictUtf8.GetBytes(value));
    }
}

/// <summary>Closed per-job-type/version schema, including its authoritative current-version resolver.</summary>
public sealed class JobSourceVersionDescriptorSchema
{
    public JobSourceVersionDescriptorSchema(
        JobType jobType,
        int schemaVersion,
        IEnumerable<JobSourceVersionFieldDefinition> fields,
        Func<JobEntity, CancellationToken, ValueTask<IReadOnlyCollection<JobSourceVersionFieldValue>?>> currentVersionResolver)
    {
        if (!Enum.IsDefined(jobType) || jobType == JobType.Surveillance || schemaVersion is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(currentVersionResolver);
        JobType = jobType;
        SchemaVersion = schemaVersion;
        CurrentVersionResolver = currentVersionResolver;
        var array = fields.ToArray();
        if (array.Length > ushort.MaxValue || array.Select(field => field.FieldId).Distinct(StringComparer.Ordinal).Count() != array.Length)
        {
            throw new ArgumentException("Descriptor schemas require unique field identifiers within the format bounds.", nameof(fields));
        }

        foreach (var field in array)
        {
            JobSourceVersionDescriptorCodec.ValidateFieldId(field.FieldId);
            if (field.Type == JobSourceVersionFieldType.Null || !Enum.IsDefined(field.Type) ||
                (field.Type == JobSourceVersionFieldType.ClosedEnumUtf8 && (field.AllowedValues is null || field.AllowedValues.Count == 0)) ||
                (field.Type != JobSourceVersionFieldType.ClosedEnumUtf8 && field.AllowedValues is not null) ||
                (field.AllowedValues?.Any(value => !value.IsNormalized(NormalizationForm.FormC)) ?? false))
            {
                throw new ArgumentException("Descriptor schema field definitions are invalid.", nameof(fields));
            }
        }

        Fields = array.ToDictionary(field => field.FieldId, StringComparer.Ordinal);
    }

    public JobType JobType { get; }
    public int SchemaVersion { get; }
    public IReadOnlyDictionary<string, JobSourceVersionFieldDefinition> Fields { get; }
    internal Func<JobEntity, CancellationToken, ValueTask<IReadOnlyCollection<JobSourceVersionFieldValue>?>> CurrentVersionResolver { get; }
}

public sealed record EncodedJobSourceVersionDescriptor(byte[] ExactBytes, byte[] Sha256, int SchemaVersion);

/// <summary>Exact binary encoder/validator for the master-approved SWIFTAID-SV1 descriptor.</summary>
public static class JobSourceVersionDescriptorCodec
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SWIFTAID-SV1");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static EncodedJobSourceVersionDescriptor Encode(
        JobType jobType,
        int schemaVersion,
        IReadOnlyCollection<JobSourceVersionFieldValue> values,
        JobSourceVersionDescriptorSchema schema)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.JobType != jobType || schema.SchemaVersion != schemaVersion || values.Count > ushort.MaxValue)
        {
            throw new ArgumentException("A matching registered descriptor schema is required.", nameof(schema));
        }

        var name = StrictUtf8.GetBytes(jobType.ToString());
        if (name.Length > ushort.MaxValue)
        {
            throw new ArgumentException("The canonical job type name is too long.", nameof(jobType));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<(byte[] Id, JobSourceVersionFieldValue Value)>();
        foreach (var value in values)
        {
            ValidateFieldId(value.FieldId);
            if (!seen.Add(value.FieldId) || !schema.Fields.TryGetValue(value.FieldId, out var definition) ||
                (value.Type != definition.Type && (value.Type != JobSourceVersionFieldType.Null || !definition.AllowsNull)) ||
                (value.Type == JobSourceVersionFieldType.Null && value.ValueBytes.Length != 0))
            {
                throw new ArgumentException("Descriptor values contain a duplicate, unknown, or incorrectly typed field.", nameof(values));
            }

            ValidateValue(value, definition);
            normalized.Add((Encoding.ASCII.GetBytes(value.FieldId), value));
        }

        if (schema.Fields.Values.Any(field => field.Required && !seen.Contains(field.FieldId)))
        {
            throw new ArgumentException("A required descriptor field is missing.", nameof(values));
        }

        normalized.Sort((left, right) => CompareOrdinal(left.Id, right.Id));
        using var stream = new MemoryStream();
        stream.Write(Magic);
        WriteUInt16(stream, checked((ushort)schemaVersion));
        WriteUInt16(stream, checked((ushort)name.Length));
        stream.Write(name);
        WriteUInt16(stream, checked((ushort)normalized.Count));
        foreach (var (id, value) in normalized)
        {
            WriteUInt16(stream, checked((ushort)id.Length));
            stream.Write(id);
            stream.WriteByte((byte)value.Type);
            WriteUInt32(stream, checked((uint)value.ValueBytes.Length));
            stream.Write(value.ValueBytes);
        }

        var bytes = stream.ToArray();
        return new EncodedJobSourceVersionDescriptor(bytes, SHA256.HashData(bytes), schemaVersion);
    }

    public static void ValidateStored(
        JobType jobType,
        int schemaVersion,
        ReadOnlySpan<byte> exactBytes,
        ReadOnlySpan<byte> expectedHash,
        JobSourceVersionDescriptorSchema schema)
    {
        var parsed = Decode(jobType, schemaVersion, exactBytes, schema);
        var recomputedHash = SHA256.HashData(exactBytes);
        if (!CryptographicOperations.FixedTimeEquals(recomputedHash, expectedHash) ||
            !parsed.Sha256.AsSpan().SequenceEqual(recomputedHash))
        {
            throw new ArgumentException("The source descriptor hash does not match its exact bytes.", nameof(expectedHash));
        }
    }

    public static EncodedJobSourceVersionDescriptor Decode(
        JobType jobType,
        int schemaVersion,
        ReadOnlySpan<byte> bytes,
        JobSourceVersionDescriptorSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.JobType != jobType || schema.SchemaVersion != schemaVersion || bytes.Length < Magic.Length + 6 ||
            !bytes[..Magic.Length].SequenceEqual(Magic))
        {
            throw new ArgumentException("The binary source descriptor header is invalid.", nameof(bytes));
        }

        var offset = Magic.Length;
        var encodedSchemaVersion = ReadUInt16(bytes, ref offset);
        var nameLength = ReadUInt16(bytes, ref offset);
        var name = ReadBytes(bytes, ref offset, nameLength);
        if (encodedSchemaVersion != schemaVersion || !name.SequenceEqual(StrictUtf8.GetBytes(jobType.ToString())))
        {
            throw new ArgumentException("The descriptor version or canonical job type does not match.", nameof(bytes));
        }

        var count = ReadUInt16(bytes, ref offset);
        var values = new List<JobSourceVersionFieldValue>(count);
        for (var i = 0; i < count; i++)
        {
            var fieldIdLength = ReadUInt16(bytes, ref offset);
            var fieldIdBytes = ReadBytes(bytes, ref offset, fieldIdLength);
            string fieldId;
            try { fieldId = StrictUtf8.GetString(fieldIdBytes); }
            catch (DecoderFallbackException exception) { throw new ArgumentException("A descriptor field id is invalid.", nameof(bytes), exception); }
            ValidateFieldId(fieldId);
            foreach (var value in fieldIdBytes)
            {
                if (value > 0x7F)
                {
                    throw new ArgumentException("Descriptor field ids must be ASCII.", nameof(bytes));
                }
            }
            var type = (JobSourceVersionFieldType)ReadByte(bytes, ref offset);
            var valueLength = ReadUInt32(bytes, ref offset);
            if (valueLength > int.MaxValue) throw new ArgumentException("A descriptor value length is too large.", nameof(bytes));
            var valueBytes = ReadBytes(bytes, ref offset, (int)valueLength).ToArray();
            values.Add(new JobSourceVersionFieldValue(fieldId, type, valueBytes));
        }

        if (offset != bytes.Length) throw new ArgumentException("The descriptor contains trailing bytes.", nameof(bytes));
        var canonical = Encode(jobType, schemaVersion, values, schema);
        if (!canonical.ExactBytes.AsSpan().SequenceEqual(bytes))
        {
            throw new ArgumentException("The source descriptor is not in canonical field order or encoding.", nameof(bytes));
        }

        return canonical;
    }

    internal static void ValidateFieldId(string fieldId)
    {
        if (string.IsNullOrEmpty(fieldId) || fieldId.Length > ushort.MaxValue ||
            fieldId.Any(character => !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')))
        {
            throw new ArgumentException("Descriptor field ids must be bounded ASCII identifiers.", nameof(fieldId));
        }
    }

    private static void ValidateValue(JobSourceVersionFieldValue value, JobSourceVersionFieldDefinition definition)
    {
        var bytes = value.ValueBytes;
        if (value.Type == JobSourceVersionFieldType.Null) return;
        var expectedLength = value.Type switch
        {
            JobSourceVersionFieldType.Uuid => 16,
            JobSourceVersionFieldType.Boolean => 1,
            JobSourceVersionFieldType.Int64 or JobSourceVersionFieldType.UtcDateTimeTicks => 8,
            JobSourceVersionFieldType.Sha256 => 32,
            _ => -1
        };
        if (expectedLength >= 0 && bytes.Length != expectedLength ||
            value.Type == JobSourceVersionFieldType.Boolean && bytes[0] > 1)
        {
            throw new ArgumentException("A descriptor field has an invalid encoded length or value.", nameof(value));
        }

        if (value.Type == JobSourceVersionFieldType.UtcDateTimeTicks)
        {
            var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            {
                throw new ArgumentException("UTC DateTime ticks are outside the supported range.", nameof(value));
            }
        }

        if (value.Type is JobSourceVersionFieldType.ClosedEnumUtf8 or JobSourceVersionFieldType.Utf8Nfc)
        {
            string text;
            try { text = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException exception) { throw new ArgumentException("Descriptor text must be strict UTF-8.", nameof(value), exception); }
            if (value.Type == JobSourceVersionFieldType.Utf8Nfc && !text.IsNormalized(NormalizationForm.FormC) ||
                value.Type == JobSourceVersionFieldType.ClosedEnumUtf8 &&
                (!text.IsNormalized(NormalizationForm.FormC) || definition.AllowedValues is null || !definition.AllowedValues.Contains(text)))
            {
                throw new ArgumentException("Descriptor text is not canonical or is not in the closed enum.", nameof(value));
            }
        }
    }

    private static int CompareOrdinal(byte[] left, byte[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            var difference = left[index].CompareTo(right[index]);
            if (difference != 0) return difference;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static void WriteUInt16(Stream stream, ushort value) { Span<byte> buffer = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(buffer, value); stream.Write(buffer); }
    private static void WriteUInt32(Stream stream, uint value) { Span<byte> buffer = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(buffer, value); stream.Write(buffer); }
    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, ref int offset) { var data = ReadBytes(bytes, ref offset, 2); return BinaryPrimitives.ReadUInt16BigEndian(data); }
    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, ref int offset) { var data = ReadBytes(bytes, ref offset, 4); return BinaryPrimitives.ReadUInt32BigEndian(data); }
    private static byte ReadByte(ReadOnlySpan<byte> bytes, ref int offset) { var data = ReadBytes(bytes, ref offset, 1); return data[0]; }
    private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> bytes, ref int offset, int length)
    {
        if (length < 0 || offset < 0 || length > bytes.Length - offset) throw new ArgumentException("The source descriptor is truncated or has invalid lengths.", nameof(bytes));
        var result = bytes.Slice(offset, length); offset = checked(offset + length); return result;
    }
}

/// <summary>Hash helpers for immutable binary source-version descriptors.</summary>
public static class JobSourceVersionHash
{
    public const int HashLength = 32;

    public static byte[] Compute(ReadOnlySpan<byte> exactDescriptorBytes)
    {
        if (exactDescriptorBytes.IsEmpty)
        {
            throw new ArgumentException("An immutable source-version descriptor is required.", nameof(exactDescriptorBytes));
        }
        return SHA256.HashData(exactDescriptorBytes);
    }

    public static bool IsValid(ReadOnlySpan<byte> hash) => hash.Length == HashLength;

    public static bool Matches(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        IsValid(expected) && IsValid(actual) && CryptographicOperations.FixedTimeEquals(expected, actual);

}

/// <summary>Gateway-owned failure classifications; worker-supplied exception text is never persisted.</summary>
public static class JobFailureCodes
{
    public const string TransientTimeout = "transient-timeout";
    public const string ProviderThrottled = "provider-throttled";
    public const string ServiceUnavailable = "service-unavailable";
    public const string WorkerLeaseExpired = "worker-lease-expired";
    public const string InvalidResultSchema = "invalid-result-schema";
    public const string CapabilityUnavailable = "capability-unavailable";
    public const string PolicyDenied = "policy-denied";
    public const string InputVersionStale = "input-version-stale";
    public const string UnsupportedJobType = "unsupported-job-type";

    public static bool IsAllowed(string value) => Allowed.Contains(value);

    public static bool IsRetryable(string value) => Retryable.Contains(value);

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        TransientTimeout,
        ProviderThrottled,
        ServiceUnavailable,
        WorkerLeaseExpired,
        InvalidResultSchema,
        CapabilityUnavailable,
        PolicyDenied,
        InputVersionStale,
        UnsupportedJobType
    };

    private static readonly HashSet<string> Retryable = new(StringComparer.Ordinal)
    {
        TransientTimeout,
        ProviderThrottled,
        ServiceUnavailable,
        WorkerLeaseExpired
    };
}

public static class JobResultAuditDecisionCodes
{
    public const string StaleSourceVersion = "stale-source-version";
    public const string StaleFencingToken = "stale-fencing-token";
    public const string DuplicateHashMismatch = "duplicate-hash-mismatch";

    public static bool IsAllowed(string value) => Allowed.Contains(value);

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        StaleSourceVersion,
        StaleFencingToken,
        DuplicateHashMismatch
    };
}

/// <summary>Resolves a descriptor schema by the exact job type and descriptor schema version.</summary>
public interface IJobSourceVersionDescriptorRegistry
{
    JobSourceVersionDescriptorSchema? Find(JobType jobType, int descriptorSchemaVersion);
}

public static class JobSourceVersionDescriptorResolver
{
    public static JobSourceVersionDescriptorSchema Require(
        IJobSourceVersionDescriptorRegistry registry,
        JobType jobType,
        int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!Enum.IsDefined(jobType) || jobType == JobType.Surveillance)
        {
            throw new UnsupportedJobSourceVersionException(jobType);
        }

        var schema = registry.Find(jobType, schemaVersion)
            ?? throw new UnsupportedJobSourceVersionException(jobType, schemaVersion);
        if (schema.JobType != jobType || schema.SchemaVersion != schemaVersion)
        {
            throw new InvalidOperationException("The descriptor registry returned a mismatched schema.");
        }

        return schema;
    }

    public static async ValueTask<EncodedJobSourceVersionDescriptor> GetCurrentAsync(
        JobEntity job,
        IJobSourceVersionDescriptorRegistry registry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        job.Validate();
        var schema = Require(registry, job.JobType, job.SourceVersionDescriptorSchemaVersion);
        var fields = await schema.CurrentVersionResolver(job, cancellationToken).ConfigureAwait(false)
            ?? throw new UnsupportedJobSourceVersionException(job.JobType, job.SourceVersionDescriptorSchemaVersion);
        return JobSourceVersionDescriptorCodec.Encode(job.JobType, schema.SchemaVersion, fields, schema);
    }

    public static async ValueTask<bool> IsCurrentAsync(
        JobEntity job,
        IJobSourceVersionDescriptorRegistry registry,
        CancellationToken cancellationToken)
    {
        var schema = Require(registry, job.JobType, job.SourceVersionDescriptorSchemaVersion);
        JobSourceVersionDescriptorCodec.ValidateStored(job.JobType, job.SourceVersionDescriptorSchemaVersion,
            job.SourceVersionDescriptorBytes, job.SourceVersionHash, schema);
        var current = await GetCurrentAsync(job, registry, cancellationToken).ConfigureAwait(false);
        return JobSourceVersionHash.Matches(job.SourceVersionHash, current.Sha256);
    }
}

/// <summary>All enabled capabilities needed to create or execute one job, with no permissive fallback.</summary>
public static class JobTypeCapabilityGate
{
    public static JobSourceVersionDescriptorSchema RequireEnabled(
        JobType jobType,
        int descriptorSchemaVersion,
        int resultSchemaVersion,
        IJobSourceVersionDescriptorRegistry sourceDescriptors,
        IJobResultSchemaValidatorRegistry resultSchemas)
    {
        var descriptor = JobSourceVersionDescriptorResolver.Require(sourceDescriptors, jobType, descriptorSchemaVersion);
        var result = resultSchemas.Find(jobType, resultSchemaVersion)
            ?? throw new UnsupportedJobResultSchemaException(jobType, resultSchemaVersion);
        if (result.JobType != jobType || result.SchemaVersion != resultSchemaVersion)
        {
            throw new InvalidOperationException("The result schema registry returned a mismatched validator.");
        }
        return descriptor;
    }

    public static JobSourceVersionDescriptorSchema RequireEnabled(
        JobEntity job,
        IJobSourceVersionDescriptorRegistry sourceDescriptors,
        IJobResultSchemaValidatorRegistry resultSchemas)
    {
        ArgumentNullException.ThrowIfNull(job);
        return RequireEnabled(job.JobType, job.SourceVersionDescriptorSchemaVersion,
            job.ExpectedResultSchemaVersion, sourceDescriptors, resultSchemas);
    }
}

/// <summary>Validates a result against one explicitly enabled job type and result schema version.</summary>
public interface IJobResultSchemaValidator
{
    JobType JobType { get; }

    int SchemaVersion { get; }

    void Validate(ReadOnlyMemory<byte> jsonUtf8);
}

/// <summary>Returns null for unsupported/disabled result schemas; no permissive fallback is allowed.</summary>
public interface IJobResultSchemaValidatorRegistry
{
    IJobResultSchemaValidator? Find(JobType jobType, int schemaVersion);
}

/// <summary>Validated JSON bytes and hash; the original byte sequence is retained unchanged.</summary>
public sealed class JobResultContent
{
    private JobResultContent(byte[] utf8Json, byte[] sha256)
    {
        Utf8Json = utf8Json;
        Sha256 = sha256;
    }

    public ReadOnlyMemory<byte> Utf8Json { get; }

    public ReadOnlyMemory<byte> Sha256 { get; }

    public static JobResultContent Validate(
        JobType jobType,
        int schemaVersion,
        ReadOnlyMemory<byte> payloadUtf8,
        JobPersistenceOptions options,
        IJobResultSchemaValidatorRegistry validators)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(validators);
        options.Validate();

        if (!Enum.IsDefined(jobType) || schemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "An enabled job type and result schema version are required.");
        }

        if (jobType == JobType.Surveillance)
        {
            throw new UnsupportedJobResultSchemaException(jobType, schemaVersion);
        }

        if (payloadUtf8.IsEmpty || payloadUtf8.Length > options.MaximumResultBytes)
        {
            throw new ArgumentException("The result payload must be non-empty and within the configured byte limit.", nameof(payloadUtf8));
        }

        var validator = validators.Find(jobType, schemaVersion)
            ?? throw new UnsupportedJobResultSchemaException(jobType, schemaVersion);
        if (validator.JobType != jobType || validator.SchemaVersion != schemaVersion)
        {
            throw new InvalidOperationException("The result-schema registry returned a mismatched validator.");
        }

        var exactBytes = payloadUtf8.ToArray();
        var stablePayload = new ReadOnlyMemory<byte>(exactBytes);

        try
        {
            _ = StrictUtf8.GetString(exactBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException("The result payload must be strictly valid UTF-8.", nameof(payloadUtf8), exception);
        }

        if (exactBytes.AsSpan().StartsWith(Utf8Bom))
        {
            throw new ArgumentException("The result payload must not contain a UTF-8 byte-order mark.", nameof(payloadUtf8));
        }

        using (var document = JsonDocument.Parse(stablePayload, StrictJsonOptions))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("A typed job result must be a JSON object.", nameof(payloadUtf8));
            }

            EnsureNoDuplicateProperties(document.RootElement);
        }

        validator.Validate(stablePayload);
        return new JobResultContent(exactBytes, SHA256.HashData(exactBytes));
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly JsonDocumentOptions StrictJsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64
    };

    private static void EnsureNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ArgumentException("The result payload must not contain duplicate JSON properties.", nameof(element));
                }

                EnsureNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                EnsureNoDuplicateProperties(item);
            }
        }
    }
}

public sealed class UnsupportedJobResultSchemaException(JobType jobType, int schemaVersion)
    : InvalidOperationException($"No result validator is enabled for job type '{jobType}' and schema version {schemaVersion}.");

public sealed class UnsupportedJobSourceVersionException(JobType jobType, int schemaVersion = 0)
    : InvalidOperationException($"No source-version descriptor schema is enabled for job type '{jobType}' and version {schemaVersion}.");
