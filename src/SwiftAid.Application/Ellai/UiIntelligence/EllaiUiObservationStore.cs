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
    IReadOnlyList<UiControlState>? ControlStates = null,
    string? CurrentNavigationId = null,
    IReadOnlyList<string>? CompletedControlIds = null,
    IReadOnlyList<string>? ValidationInputIds = null,
    IReadOnlyList<UiInputState>? InputStates = null,
    Guid? CurrentCaseId = null);

public interface IEllaiUiObservationStore
{
    /// <summary>Returns false if a newer server-stamped report was already stored.</summary>
    Task<bool> RecordAsync(StoredUiObservationReport report, CancellationToken ct);

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

    public Task<bool> RecordAsync(StoredUiObservationReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(report.SessionId);
        ct.ThrowIfCancellationRequested();
        if (!(new UiObservationReport(report.ViewInstanceId, report.ContextStamp, report.Platform,
                report.MountedControlIds, report.ControlStates, report.CurrentNavigationId, report.CompletedControlIds)
                { ValidationInputIds = report.ValidationInputIds, InputStates = report.InputStates, CurrentCaseId = report.CurrentCaseId }).IsValid())
            throw new ArgumentException("Invalid UI observation report.", nameof(report));
        var cleaned = report.MountedControlIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(MaxMountedControls)
            .ToList();
        lock (_gate)
        {
            // Concurrent requests can reach this lock out of order. An older
            // server-stamped observation must not replace newer UI evidence.
            if (_latest.TryGetValue(report.SessionId, out var current) && current.ReceivedAtUtc > report.ReceivedAtUtc)
                return Task.FromResult(false);
            if (!_latest.ContainsKey(report.SessionId) && _latest.Count >= 4096)
                _latest.Remove(_latest.MinBy(entry => entry.Value.ReceivedAtUtc).Key);
            _latest[report.SessionId] = report with
            {
                MountedControlIds = cleaned.AsReadOnly(),
                ControlStates = report.ControlStates?.ToList().AsReadOnly(),
                CompletedControlIds = report.CompletedControlIds?.ToList().AsReadOnly(),
                ValidationInputIds = report.ValidationInputIds?.ToList().AsReadOnly(),
                InputStates = report.InputStates?.ToList().AsReadOnly()
            };
        }

        return Task.FromResult(true);
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
