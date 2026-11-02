using SwiftAid.Contracts.Commands;

namespace SwiftAid.Infrastructure.Persistence.Entities.Cases;

/// <summary>Current assistance state; event journal remains the audit record.</summary>
public sealed class ManagedCaseAssistanceEntity
{
    public Guid AssistanceId { get; set; }
    public Guid CaseId { get; set; }
    public required string Need { get; set; }
    public required string ResponsibleActorId { get; set; }
    public required string RequestedByActorId { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset? DueAtUtc { get; set; }
    public AssistanceStatus Status { get; set; }
    public required string LastNote { get; set; }
    public string? AcceptedByActorId { get; set; }
    public string? ProvidedByActorId { get; set; }
    public string? ReceiptRecordedByActorId { get; set; }
    public string? ReceiptMethod { get; set; }
    public string? ReceiptContext { get; set; }
    public long LastEventVersion { get; set; }
}
