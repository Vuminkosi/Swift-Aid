namespace SwiftAid.Infrastructure.Persistence.Ellai;

/// <summary>Authoritative row for one Ellai session. Version is the fencing
/// concurrency token; every mutation compares and sets it.</summary>
public sealed class EllaiSessionEntity
{
    public string SessionId { get; set; } = "";
    public long Version { get; set; }
    public string Mode { get; set; } = "";
    public string Status { get; set; } = "";
    public int CyclesUsed { get; set; }
    public int CyclesAllowed { get; set; }
    public long ControlFence { get; set; }
    public long InteractionEpoch { get; set; }
    public long SpeechEpoch { get; set; }
    public string ScopeEpoch { get; set; } = "";
    public string? ActiveInvestigationId { get; set; }
    public string? PendingCheckpointId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Accepted utterance identity: one row per (session,
/// client-utterance) pair enforces §7.1 idempotency across restarts and
/// gateway instances. Replays read this row instead of consuming a cycle.</summary>
public sealed class EllaiUtteranceEntity
{
    public string SessionId { get; set; } = "";
    public string ClientUtteranceId { get; set; } = "";
    public DateTimeOffset AcceptedAtUtc { get; set; }
}

/// <summary>Durable accepted-turn checkpoint linkage.</summary>
public sealed class EllaiCheckpointEntity
{
    public string CheckpointId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public long SessionVersion { get; set; }
    public string? TurnId { get; set; }
    public string TopicRefsJson { get; set; } = "[]";
    public string PendingReceiptIdsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAtUtc { get; set; }
}
