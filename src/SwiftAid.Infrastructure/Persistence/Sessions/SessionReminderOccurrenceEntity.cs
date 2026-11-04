namespace SwiftAid.Infrastructure.Persistence.Sessions;

/// <summary>
/// One computed reminder occurrence for a community session. Dispatch/execution
/// of reminders is explicitly OUT of scope (deferred to the P6.3 durable-reminder
/// lane): this table only records what is due. <c>Sent</c> rows are never modified
/// or deleted by the session service.
/// </summary>
public sealed class SessionReminderOccurrenceEntity
{
    public Guid OccurrenceId { get; set; }
    public Guid SessionId { get; set; }
    public DateTimeOffset RemindAtUtc { get; set; }
    public int LeadHours { get; set; }

    /// <summary>Pending, Suppressed, or Sent.</summary>
    public string Status { get; set; } = "Pending";

    /// <summary>Session version the occurrence was computed from (checked, not a FK).</summary>
    public long Version { get; set; }
}
