using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Gateway.Ellai;

public sealed class EllaiFollowUpScheduleOptions
{
    public bool Enabled { get; set; }
    public string[] Workspaces { get; set; } = [];
    public string PolicyVersion { get; set; } = "";
    public int BatchSize { get; set; } = 10;
    public int PollSeconds { get; set; } = 60;
    public int CadenceHours { get; set; } = 4;
    public long ConfigurationRevision { get; set; } = 1;

    public void Validate()
    {
        if (Workspaces is null || Workspaces.Length is < 1 or > 100 ||
            Workspaces.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 120 || x != x.Trim()) ||
            Workspaces.Distinct(StringComparer.Ordinal).Count() != Workspaces.Length ||
            string.IsNullOrWhiteSpace(PolicyVersion) || PolicyVersion.Length > 120 ||
            BatchSize is < 1 or > 100 || PollSeconds is < 30 or > 3600 || CadenceHours is not (4 or 8) || ConfigurationRevision < 1)
            throw new InvalidOperationException("Follow-up scheduling requires bounded, explicit workspace and policy configuration.");
    }
}

/// <summary>Non-sensitive counters for one scheduled attempt, including failed attempts.</summary>
public sealed class EllaiFollowUpSweepProgress
{
    public Guid RunId { get; } = Guid.NewGuid();
    public int WorkTurns { get; internal set; }
    public int ChangesCommitted { get; internal set; }
}

/// <summary>A single bounded sweep; callers supply an isolated framework session for each turn.</summary>
public sealed class EllaiFollowUpSweep(SwiftAidDbContext db, EllaiFollowUpWork work,
    IOptions<EllaiFollowUpOptions> followUp)
{
    public async Task<string> RunAsync(string workspaceId, EllaiFollowUpScheduleOptions options,
        Func<CancellationToken, Task<AgentSession>> openTurn, EllaiFollowUpSweepProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!options.Enabled) return "disabled";
        options.Validate();
        if (!options.Workspaces.Contains(workspaceId, StringComparer.Ordinal)) return "disabled";
        var settings = followUp.Value;
        if (!settings.Enabled) throw new InvalidOperationException("The scoped follow-up worker is disabled.");
        settings.Validate();
        var authorized = await db.WorkspaceOrganizations.AsNoTracking().AnyAsync(link =>
            link.WorkspaceId == workspaceId && link.Status == PersistentResourceStatus.Active &&
            settings.Organizations.Contains(link.OrganizationId) &&
            db.Workspaces.Any(w => w.WorkspaceId == workspaceId && w.Status == PersistentResourceStatus.Active) &&
            db.Organizations.Any(o => o.OrganizationId == link.OrganizationId && o.Status == PersistentResourceStatus.Active), ct);
        if (!authorized) return "scope-unavailable";
        var schedule = new PostgresEllaiOperationalScheduleStore(db);
        // Stable workspace jitter spreads new schedules across the first five minutes.
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("ops.followup:" + workspaceId));
        var jitter = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash) % 300;
        await schedule.ReconcileConfigurationAsync(workspaceId, "ops.followup", options.CadenceHours, options.PolicyVersion,
            options.ConfigurationRevision, DateTimeOffset.UtcNow.AddSeconds(jitter), ct);
        var configured = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM ellai_operational_schedules
            WHERE workspace_id = {workspaceId} AND role_id = 'ops.followup'
                AND cadence_hours = {options.CadenceHours} AND policy_version = {options.PolicyVersion}
                AND configuration_revision = {options.ConfigurationRevision}
            """).SingleAsync(ct);
        if (configured != 1)
        {
            var samePolicy = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM ellai_operational_schedules
                WHERE workspace_id = {workspaceId} AND role_id = 'ops.followup' AND policy_version = {options.PolicyVersion}
                """).SingleAsync(ct);
            return samePolicy == 1 ? "configuration-mismatch" : "policy-mismatch";
        }
        var lease = await schedule.TryClaimAsync(workspaceId, "ops.followup", TimeSpan.FromMinutes(5), ct, options.ConfigurationRevision);
        if (lease is null) return "not-due";
        var finished = false;
        try
        {
            if (!string.Equals(lease.PolicyVersion, options.PolicyVersion, StringComparison.Ordinal)) return "policy-mismatch";
            for (var index = 0; index < options.BatchSize; index++)
            {
                progress.WorkTurns++;
                var session = await openTurn(ct);
                EllaiFencedWorkOutcome outcome;
                try
                {
                    outcome = await work.RunOneAsync(lease, progress.RunId, Guid.NewGuid(), ct);
                    if (outcome == EllaiFencedWorkOutcome.Committed) progress.ChangesCommitted++;
                }
                finally
                {
                    if ((object)session is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                    else if ((object)session is IDisposable disposable) disposable.Dispose();
                    GC.KeepAlive(session);
                }
                if (outcome == EllaiFencedWorkOutcome.StaleLease) return "stale-lease";
                if (outcome == EllaiFencedWorkOutcome.Retry) continue;
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
