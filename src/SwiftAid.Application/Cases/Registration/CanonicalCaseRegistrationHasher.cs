using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Geography;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Cases.Registration;

/// <summary>Canonical receipt protocol for new staff registrations; distinct from the external wire envelope.</summary>
public static class CanonicalCaseRegistrationHasher
{
    public const string ProtocolId = "swiftaid.case-command-receipt";
    public const int CurrentReceiptProtocolVersion = 1;
    public const int CurrentWireCommandSchemaVersion = 1;
    public const string CommandType = "RegisterManagedCase";

    public static CanonicalCaseRegistrationSnapshot CreateSnapshot(
        ActorContext actor,
        RegisterManagedCaseRequest request,
        int receiptProtocolVersion = CurrentReceiptProtocolVersion,
        int wireCommandSchemaVersion = CurrentWireCommandSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (receiptProtocolVersion != CurrentReceiptProtocolVersion)
        {
            throw new NotSupportedException("The registration receipt protocol is not supported.");
        }

        if (wireCommandSchemaVersion != CurrentWireCommandSchemaVersion)
        {
            throw new NotSupportedException("The registration command schema is not supported.");
        }

        RequireCanonicalText(actor.ActorId, nameof(actor));
        if (request.CommandId == Guid.Empty) throw new ArgumentException("A command identifier is required.", nameof(request));
        RequireCanonicalText(request.WorkspaceId, nameof(request.WorkspaceId));

        var report = request.Report switch
        {
            CaseReportEventData.HumanV2 human => SnapshotHuman(human),
            CaseReportEventData.AnimalV2 animal => SnapshotAnimal(animal),
            CaseReportEventData.LegacyV1 => throw new NotSupportedException("Legacy report data cannot be used for a new registration."),
            _ => throw new NotSupportedException("The report type is not supported by registration receipt protocol v1.")
        };

        var canonicalBytes = WriteCanonicalRequest(
            actor.ActorId,
            request.CommandId,
            request.WorkspaceId,
            receiptProtocolVersion,
            wireCommandSchemaVersion,
            report);
        return new CanonicalCaseRegistrationSnapshot(
            receiptProtocolVersion,
            wireCommandSchemaVersion,
            actor.ActorId,
            request.CommandId,
            request.WorkspaceId,
            request.Report,
            GetConfirmations(request.Report),
            SHA256.HashData(canonicalBytes));
    }

    private static ReportSnapshot SnapshotHuman(CaseReportEventData.HumanV2 value)
    {
        ArgumentNullException.ThrowIfNull(value.Report);
        ArgumentNullException.ThrowIfNull(value.Confirmations);
        value.Validate();
        return new ReportSnapshot(
            "HumanV2",
            writer => WriteHuman(writer, value.Report),
            writer => WriteConfirmations(writer, value.Confirmations));
    }

    private static ReportSnapshot SnapshotAnimal(CaseReportEventData.AnimalV2 value)
    {
        ArgumentNullException.ThrowIfNull(value.Report);
        ArgumentNullException.ThrowIfNull(value.Confirmations);
        value.Validate();
        return new ReportSnapshot(
            "AnimalV2",
            writer => WriteAnimal(writer, value.Report),
            writer => WriteConfirmations(writer, value.Confirmations));
    }

    private static byte[] WriteCanonicalRequest(
        string actorId,
        Guid commandId,
        string workspaceId,
        int receiptProtocolVersion,
        int wireCommandSchemaVersion,
        ReportSnapshot report)
    {
        var buffer = new ArrayBufferWriter<byte>();
        // This private byte protocol is deliberately separate from command/event wire JSON.
        // Property names and order are pinned by the normative UTF-8 golden vector test.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("protocolId", ProtocolId);
            writer.WriteNumber("receiptProtocolVersion", receiptProtocolVersion);
            writer.WriteNumber("wireCommandSchemaVersion", wireCommandSchemaVersion);
            writer.WriteString("actorId", actorId);
            writer.WriteString("commandId", commandId.ToString("D"));
            writer.WriteString("commandType", CommandType);
            writer.WriteString("workspaceId", workspaceId);
            writer.WritePropertyName("report");
            report.WriteReport(writer);
            writer.WritePropertyName("confirmations");
            report.WriteConfirmations(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteHuman(Utf8JsonWriter writer, HumanReport report)
    {
        writer.WriteStartObject();
        writer.WriteString("reportId", report.ReportId.ToString("D"));
        WriteContext(writer, report.Context);
        WriteReportedFact(writer, "pseudonymousSubjectReference", report.PseudonymousSubjectReference, static (w, v) => w.WriteStringValue(v));
        WriteReportedFact(writer, "ageYears", report.AgeYears, static (w, v) => w.WriteNumberValue(v));
        WriteReportedFact(writer, "reportedConcern", report.ReportedConcern, static (w, v) => w.WriteStringValue(v));
        WriteEvidence(writer, report.Evidence);
        writer.WriteEndObject();
    }

    private static void WriteAnimal(Utf8JsonWriter writer, AnimalReport report)
    {
        writer.WriteStartObject();
        writer.WriteString("reportId", report.ReportId.ToString("D"));
        WriteContext(writer, report.Context);
        writer.WriteString("subjectScope", EnumName(report.SubjectScope));
        WriteReportedFact(writer, "species", report.Species, static (w, v) => w.WriteStringValue(v));
        WriteReportedFact(writer, "holdingReference", report.HoldingReference, static (w, v) => w.WriteStringValue(v));
        WriteEvidence(writer, report.Evidence);
        WriteAnimalCount(writer, "animalsAtRisk", report.AnimalsAtRisk);
        WriteAnimalCount(writer, "animalsAffected", report.AnimalsAffected);
        WriteAnimalCount(writer, "animalsDead", report.AnimalsDead);
        writer.WriteEndObject();
    }

    private static void WriteContext(Utf8JsonWriter writer, ReportContext context)
    {
        writer.WritePropertyName("context");
        writer.WriteStartObject();
        WriteReportedFact(writer, "occurrence", context.Occurrence, WriteReportedTime);
        WriteNullableTimestamp(writer, "capturedAtUtc", context.CapturedAtUtc);
        WriteNullableString(writer, "capturedOffset", context.CapturedOffset);
        WriteNullableString(writer, "capturedZoneId", context.CapturedZoneId);
        writer.WriteString("capturePrecision", EnumName(context.CapturePrecision));
        writer.WritePropertyName("location");
        writer.WriteStartObject();
        writer.WriteString("countryCode", context.Location.CountryCode);
        WritePlace(writer, "region", context.Location.Region);
        WritePlace(writer, "area", context.Location.Area);
        WritePlace(writer, "locality", context.Location.Locality);
        WriteNullableNumber(writer, "latitude", context.Location.Latitude);
        WriteNullableNumber(writer, "longitude", context.Location.Longitude);
        WriteNullableNumber(writer, "accuracyMetres", context.Location.AccuracyMetres);
        writer.WriteString("source", EnumName(context.Location.Source));
        writer.WriteString("precision", EnumName(context.Location.Precision));
        WriteNullableString(writer, "catalogueVersion", context.Location.CatalogueVersion);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WritePlace(Utf8JsonWriter writer, string name, OptionalPlace place)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        WriteNullableString(writer, "reportedName", place.ReportedName);
        WriteNullableString(writer, "normalizedId", place.NormalizedId);
        writer.WriteString("presence", EnumName(place.Presence));
        writer.WriteEndObject();
    }

    private static void WriteEvidence(Utf8JsonWriter writer, ReportEvidence evidence)
    {
        writer.WritePropertyName("evidence");
        writer.WriteStartObject();
        writer.WritePropertyName("narrative");
        if (evidence.Narrative is null) writer.WriteNullValue();
        else WriteReportedTextValue(writer, evidence.Narrative);
        writer.WriteStartArray("observations");
        foreach (var observation in evidence.Observations)
        {
            WriteReportedTextValue(writer, observation);
        }

        writer.WriteEndArray();
        writer.WritePropertyName("originalAudio");
        if (evidence.OriginalAudio is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("reference", evidence.OriginalAudio.Reference);
            WriteProvenance(writer, evidence.OriginalAudio.Provenance);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteReportedTextValue(Utf8JsonWriter writer, ReportedText text)
    {
        writer.WriteStartObject();
        writer.WriteString("text", text.Text);
        WriteProvenance(writer, text.Provenance);
        writer.WriteEndObject();
    }

    private static void WriteProvenance(Utf8JsonWriter writer, ReportProvenance provenance)
    {
        writer.WritePropertyName("provenance");
        writer.WriteStartObject();
        writer.WriteString("source", EnumName(provenance.Source));
        writer.WriteString("stableReference", provenance.StableReference);
        writer.WriteEndObject();
    }

    private static void WriteAnimalCount(Utf8JsonWriter writer, string name, AnimalCount count)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        WriteReportedValue(writer, "value", count.Value, static (w, v) => w.WriteNumberValue(v));
        WriteProvenance(writer, count.Provenance);
        WriteNullableString(writer, "populationReference", count.PopulationReference);
        writer.WritePropertyName("observationPeriod");
        if (count.ObservationPeriod is null) writer.WriteNullValue();
        else WriteReportedFactValue(writer, count.ObservationPeriod, WriteReportedTime);
        writer.WriteEndObject();
    }

    private static void WriteReportedFact<T>(Utf8JsonWriter writer, string name, ReportedFact<T> fact, Action<Utf8JsonWriter, T> writeValue)
    {
        writer.WritePropertyName(name);
        WriteReportedFactValue(writer, fact, writeValue);
    }

    private static void WriteReportedFactValue<T>(Utf8JsonWriter writer, ReportedFact<T> fact, Action<Utf8JsonWriter, T> writeValue)
    {
        writer.WriteStartObject();
        WriteReportedValue(writer, "value", fact.Value, writeValue);
        WriteProvenance(writer, fact.Provenance);
        writer.WriteEndObject();
    }

    private static void WriteReportedValue<T>(Utf8JsonWriter writer, string name, ReportedValue<T> value, Action<Utf8JsonWriter, T> writeKnownValue)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("state", EnumName(value.State));
        writer.WritePropertyName("value");
        if (value.HasValue) writeKnownValue(writer, value.Value);
        else writer.WriteNullValue();
        writer.WriteEndObject();
    }

    private static void WriteReportedTime(Utf8JsonWriter writer, ReportedTime time)
    {
        writer.WriteStartObject();
        WriteNullableTimestamp(writer, "value", time.Value);
        WriteNullableDate(writer, "localDate", time.LocalDate);
        WriteNullableDate(writer, "rangeStart", time.RangeStart);
        WriteNullableDate(writer, "rangeEnd", time.RangeEnd);
        WriteNullableString(writer, "originalOffset", time.OriginalOffset);
        WriteNullableString(writer, "zoneId", time.ZoneId);
        writer.WriteString("precision", EnumName(time.Precision));
        writer.WriteEndObject();
    }

    private static void WriteConfirmations(Utf8JsonWriter writer, ReportConfirmations confirmations)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("reportReviewConfirmed", confirmations.ReportReviewConfirmed);
        writer.WriteString("occurrenceCountryCode", confirmations.OccurrenceCountryCode);
        writer.WriteString("occurrenceCountryCatalogueVersion", confirmations.OccurrenceCountryCatalogueVersion);
        writer.WriteBoolean("occurrenceCountryConfirmed", confirmations.OccurrenceCountryConfirmed);
        writer.WriteBoolean("processingConfirmed", confirmations.ProcessingConfirmed);
        writer.WriteString("processingPolicyReference", confirmations.ProcessingPolicyReference);
        writer.WriteString("processingPolicyVersion", confirmations.ProcessingPolicyVersion);
        WriteNullableBoolean(writer, "recordingConfirmed", confirmations.RecordingConfirmed);
        WriteNullableString(writer, "recordingPolicyReference", confirmations.RecordingPolicyReference);
        WriteNullableString(writer, "recordingPolicyVersion", confirmations.RecordingPolicyVersion);
        writer.WriteEndObject();
    }

    private static string EnumName<TEnum>(TEnum value) where TEnum : struct, Enum =>
        Enum.IsDefined(value) ? value.ToString() : throw new ArgumentOutOfRangeException(nameof(value));

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static void WriteNullableBoolean(Utf8JsonWriter writer, string name, bool? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteBoolean(name, value.Value);
    }

    private static void WriteNullableTimestamp(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    private static void WriteNullableDate(Utf8JsonWriter writer, string name, DateOnly? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteNumber(name, value.Value);
    }

    private static void RequireCanonicalText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A non-empty canonical identifier is required.", parameterName);
        }
    }

    private static ReportConfirmations GetConfirmations(CaseReportEventData report) => report switch
    {
        CaseReportEventData.HumanV2 human => human.Confirmations,
        CaseReportEventData.AnimalV2 animal => animal.Confirmations,
        _ => throw new NotSupportedException("A supported report confirmation set is required.")
    };

    private sealed record ReportSnapshot(
        string Kind,
        Action<Utf8JsonWriter> WriteReportBody,
        Action<Utf8JsonWriter> WriteConfirmationsBody)
    {
        public void WriteReport(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", Kind);
            writer.WritePropertyName("data");
            WriteReportBody(writer);
            writer.WriteEndObject();
        }

        public void WriteConfirmations(Utf8JsonWriter writer) => WriteConfirmationsBody(writer);
    }
}

/// <summary>Immutable, execution-ready registration data bound to its canonical receipt hash.</summary>
public sealed class CanonicalCaseRegistrationSnapshot
{
    private readonly byte[] _canonicalPayloadHash;

    internal CanonicalCaseRegistrationSnapshot(
        int receiptProtocolVersion,
        int wireCommandSchemaVersion,
        string actorId,
        Guid commandId,
        string workspaceId,
        CaseReportEventData report,
        ReportConfirmations confirmations,
        byte[] canonicalPayloadHash)
    {
        ProtocolId = CanonicalCaseRegistrationHasher.ProtocolId;
        ReceiptProtocolVersion = receiptProtocolVersion;
        WireCommandSchemaVersion = wireCommandSchemaVersion;
        ActorId = actorId;
        CommandId = commandId;
        WorkspaceId = workspaceId;
        Report = report;
        Confirmations = confirmations;
        _canonicalPayloadHash = canonicalPayloadHash.ToArray();
    }

    public string ProtocolId { get; }
    public int ReceiptProtocolVersion { get; }
    public int WireCommandSchemaVersion { get; }
    public string ActorId { get; }
    public Guid CommandId { get; }
    public string WorkspaceId { get; }
    public CaseReportEventData Report { get; }
    public ReportConfirmations Confirmations { get; }
    public ImmutableArray<byte> CanonicalPayloadHash => ImmutableArray.CreateRange(_canonicalPayloadHash);

    public override string ToString() => $"CanonicalCaseRegistrationSnapshot(v{ReceiptProtocolVersion}, {CommandId:D})";
}
