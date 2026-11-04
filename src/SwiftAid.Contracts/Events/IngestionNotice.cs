namespace SwiftAid.Contracts.Events;

/// <summary>Opaque, versioned notification of committed source data. Never contains report facts or authority.</summary>
public sealed record IngestionNotice(
    int SchemaVersion,
    Guid MessageId,
    string SourceKind,
    Guid SourceId,
    long SourceVersion,
    long ReportRevision,
    string EventType,
    DateTimeOffset RecordedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
    public const string StreamName = "SWIFTAID_INGESTION";
    public const string SubjectPrefix = "swiftaid.ingestion.v1";

    public bool IsValid() => SchemaVersion == CurrentSchemaVersion &&
        MessageId != Guid.Empty && SourceId != Guid.Empty &&
        SourceKind is "case" or "intake" && SourceVersion > 0 && ReportRevision > 0 &&
        !string.IsNullOrWhiteSpace(EventType) && EventType.Length <= 100 &&
        EventType.All(character => char.IsAsciiLetterOrDigit(character)) &&
        RecordedAtUtc.Offset == TimeSpan.Zero;
}
