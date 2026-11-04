using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Gateway.Ellai;

public sealed class EllaiQualityScheduleOptions
{
    public bool Enabled { get; set; }
    public string[] Workspaces { get; set; } = [];
    public string PolicyVersion { get; set; } = "";
    public int BatchSize { get; set; } = 10;
    public int PollSeconds { get; set; } = 60;

    public void Validate()
    {
        if (Workspaces is null || Workspaces.Length is < 1 or > 100 ||
            Workspaces.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 120 || x != x.Trim()) ||
            Workspaces.Distinct(StringComparer.Ordinal).Count() != Workspaces.Length ||
            string.IsNullOrWhiteSpace(PolicyVersion) || PolicyVersion.Length > 120 ||
            BatchSize is < 1 or > 100 || PollSeconds is < 30 or > 3600)
            throw new InvalidOperationException("Quality scheduling requires bounded, explicit workspace and policy configuration.");
    }
}

/// <summary>A single bounded sweep; callers supply an isolated framework session for each turn.</summary>
public sealed class EllaiQualitySweep(SwiftAidDbContext db, EllaiQualityWork work,
    IOptions<ClassificationOptions> classification)
{
    public async Task<string> RunAsync(string workspaceId, EllaiQualityScheduleOptions options,
        Func<CancellationToken, Task<AgentSession>> openTurn, CancellationToken ct)
    {
        if (!options.Enabled) return "disabled";
        options.Validate();
        if (!options.Workspaces.Contains(workspaceId, StringComparer.Ordinal)) return "disabled";
        var settings = classification.Value;
        if (!settings.Enabled) throw new InvalidOperationException("The scoped classification worker is disabled.");
        settings.Validate();
        var authorized = await db.WorkspaceOrganizations.AsNoTracking().AnyAsync(link =>
            link.WorkspaceId == workspaceId && link.Status == PersistentResourceStatus.Active &&
            settings.Organizations.Contains(link.OrganizationId) &&
            db.Workspaces.Any(w => w.WorkspaceId == workspaceId && w.Status == PersistentResourceStatus.Active) &&
            db.Organizations.Any(o => o.OrganizationId == link.OrganizationId && o.Status == PersistentResourceStatus.Active), ct);
        if (!authorized) return "scope-unavailable";
        var schedule = new PostgresEllaiOperationalScheduleStore(db);
        // Stable workspace jitter spreads new schedules across the first five minutes.
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(workspaceId));
        var jitter = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash) % 300;
        await schedule.CreateAsync(workspaceId, "ops.quality", 4, options.PolicyVersion,
            DateTimeOffset.UtcNow.AddSeconds(jitter), ct);
        var lease = await schedule.TryClaimAsync(workspaceId, "ops.quality", TimeSpan.FromMinutes(5), ct);
        if (lease is null) return "not-due";
        var finished = false;
        try
        {
            if (!string.Equals(lease.PolicyVersion, options.PolicyVersion, StringComparison.Ordinal)) return "policy-mismatch";
            var runId = Guid.NewGuid();
            for (var index = 0; index < options.BatchSize; index++)
            {
                var session = await openTurn(ct);
                EllaiFencedWorkOutcome outcome;
                try { outcome = await work.RunOneAsync(lease, runId, Guid.NewGuid(), ct); }
                finally
                {
                    if ((object)session is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                    else if ((object)session is IDisposable disposable) disposable.Dispose();
                    GC.KeepAlive(session);
                }
                if (outcome == EllaiFencedWorkOutcome.StaleLease) return "stale-lease";
                if (outcome == EllaiFencedWorkOutcome.NoChange)
                {
                    finished = await schedule.CompleteAsync(lease, ct);
                    return finished ? "complete" : "stale-lease";
                }
            }
            return "batch-limit";
        }
        finally
        {
            if (!finished)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await schedule.AbandonAsync(lease, timeout.Token); }
                catch (Exception error) when (error is not OutOfMemoryException) { /* expiry permits recovery */ }
            }
        }
    }
}
