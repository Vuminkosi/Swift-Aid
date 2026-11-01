using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Latest client-posted mounted-control report for a session, with
/// the server receipt stamp freshness derives from.</summary>
public sealed record StoredUiObservationReport(
    string SessionId,
    string ViewInstanceId,
    string ContextStamp,
    string Platform,
    IReadOnlyList<string> MountedControlIds,
    DateTimeOffset ReceivedAtUtc,
    IReadOnlyList<UiControlState>? ControlStates = null);

public interface IEllaiUiObservationStore
{
    Task RecordAsync(StoredUiObservationReport report, CancellationToken ct);

    /// <summary>Latest report for the session, or null when the client has
    /// not posted one. Sessions never see each other's reports.</summary>
    Task<StoredUiObservationReport?> LatestAsync(string sessionId, CancellationToken ct);
}

/// <summary>Thread-safe in-memory observation channel for tests and local
/// development. A restarted process loses undelivered reports, never
/// business effects; the model then reads an honest authorized empty.</summary>
public sealed class InMemoryEllaiUiObservationStore : IEllaiUiObservationStore
{
    public const int MaxMountedControls = 20;

    private readonly object _gate = new();
    private readonly Dictionary<string, StoredUiObservationReport> _latest = new(StringComparer.Ordinal);

    public Task RecordAsync(StoredUiObservationReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(report.SessionId);
        ct.ThrowIfCancellationRequested();
        if (!new UiObservationReport(report.ViewInstanceId, report.ContextStamp, report.Platform,
                report.MountedControlIds, report.ControlStates).IsValid())
            throw new ArgumentException("Invalid UI observation report.", nameof(report));
        var cleaned = report.MountedControlIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(MaxMountedControls)
            .ToList();
        lock (_gate)
        {
            if (!_latest.ContainsKey(report.SessionId) && _latest.Count >= 4096)
                _latest.Remove(_latest.MinBy(entry => entry.Value.ReceivedAtUtc).Key);
            _latest[report.SessionId] = report with
            {
                MountedControlIds = cleaned.AsReadOnly(),
                ControlStates = report.ControlStates?.ToList().AsReadOnly()
            };
        }

        return Task.CompletedTask;
    }

    public Task<StoredUiObservationReport?> LatestAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            _latest.TryGetValue(sessionId, out var report);
            return Task.FromResult(report);
        }
    }
}
