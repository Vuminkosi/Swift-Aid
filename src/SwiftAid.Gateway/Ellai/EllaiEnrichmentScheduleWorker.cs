using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Gateway.Ellai;

public sealed class EllaiEnrichmentScheduleOptions
{
    public bool Enabled { get; set; }
    public string[] Workspaces { get; set; } = [];
    public string PolicyVersion { get; set; } = "";
    public int CadenceHours { get; set; } = 4;
    public long ConfigurationRevision { get; set; } = 1;
    public int BatchSize { get; set; } = 100;
    public int PollSeconds { get; set; } = 60;

    public void Validate()
    {
        if (Workspaces is null || Workspaces.Length is < 1 or > 100 ||
            Workspaces.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 120 || value != value.Trim()) ||
            Workspaces.Distinct(StringComparer.Ordinal).Count() != Workspaces.Length ||
            string.IsNullOrWhiteSpace(PolicyVersion) || PolicyVersion.Length > 120 ||
            CadenceHours is not (4 or 8) || ConfigurationRevision < 1 || BatchSize is < 1 or > 100 || PollSeconds is < 30 or > 3600)
            throw new InvalidOperationException("Enrichment scheduling requires bounded, explicit scope and configuration.");
    }
}

/// <summary>Schedules bounded classification recovery without invoking inference in the Gateway.</summary>
public sealed class EllaiEnrichmentScheduleWorker(IServiceScopeFactory scopes,
    IOptions<EllaiEnrichmentScheduleOptions> configuration, IOptions<ClassificationOptions> classification,
    ILogger<EllaiEnrichmentScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = configuration.Value;
        if (!options.Enabled) return;
        options.Validate();
        var settings = classification.Value;
        if (!settings.Enabled) throw new InvalidOperationException("Enrichment scheduling requires classification scope.");
        settings.Validate();
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var workspace in options.Workspaces)
            {
                try { await RunWorkspaceAsync(workspace, options, settings, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is not OutOfMemoryException)
                { logger.LogWarning("Ellai enrichment schedule deferred: {FailureType}", error.GetType().Name); }
            }
            await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunWorkspaceAsync(string workspace, EllaiEnrichmentScheduleOptions options,
        ClassificationOptions settings, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<SwiftAidDbContext>();
        var organizations = await db.WorkspaceOrganizations.AsNoTracking()
            .Where(link => link.WorkspaceId == workspace && link.Status == PersistentResourceStatus.Active &&
                settings.Organizations.Contains(link.OrganizationId) &&
                db.Workspaces.Any(item => item.WorkspaceId == workspace && item.Status == PersistentResourceStatus.Active) &&
                db.Organizations.Any(item => item.OrganizationId == link.OrganizationId && item.Status == PersistentResourceStatus.Active))
            .OrderBy(link => link.OrganizationId).Select(link => link.OrganizationId).ToArrayAsync(ct).ConfigureAwait(false);
        if (organizations.Length == 0) return;
        var schedule = new PostgresEllaiOperationalScheduleStore(db);
        var jitter = BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("ops.enrichment:" + workspace))) % 300;
        await schedule.ReconcileConfigurationAsync(workspace, "ops.enrichment", options.CadenceHours, options.PolicyVersion,
            options.ConfigurationRevision, DateTimeOffset.UtcNow.AddSeconds(jitter), ct).ConfigureAwait(false);
        var matches = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM ellai_operational_schedules
            WHERE workspace_id = {workspace} AND role_id = 'ops.enrichment' AND cadence_hours = {options.CadenceHours}
                AND policy_version = {options.PolicyVersion} AND configuration_revision = {options.ConfigurationRevision}
            """).SingleAsync(ct).ConfigureAwait(false);
        if (matches != 1) { logger.LogWarning("Ellai enrichment schedule configuration mismatch."); return; }
        var lease = await schedule.TryClaimAsync(workspace, "ops.enrichment", TimeSpan.FromMinutes(5), ct,
            options.ConfigurationRevision).ConfigureAwait(false);
        if (lease is null) return;
        var complete = false;
        var runId = Guid.NewGuid();
        var workTurns = 0;
        var jobsCreated = 0;
        try
        {
            var factory = services.GetRequiredService<IEllaiFrameworkAgentFactory>();
            var client = services.GetRequiredKeyedService<IChatClient>(EllaiReasoningRegistration.FrameworkClientKey);
            var agent = factory.Create(client, new ChatClientAgentOptions
            {
                Name = "EllaiEnrichment", UseProvidedChatClientAsIs = true,
                ChatHistoryProvider = new InMemoryChatHistoryProvider()
            });
            var work = services.GetRequiredService<EllaiEnrichmentWork>();
            foreach (var organization in organizations)
            {
                workTurns++;
                var session = await agent.CreateSessionAsync(cancellationToken: ct).ConfigureAwait(false);
                try
                {
                    var identity = string.Join('\n', "ellai-enrichment-job/1", workspace, organization,
                        lease.DueAtUtc.ToUniversalTime().ToString("O"), settings.Profile.Hash, lease.PolicyVersion);
                    var jobId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
                    var outcome = await work.RunOneAsync(lease, organization, jobId, options.BatchSize, ct).ConfigureAwait(false);
                    if (outcome == EllaiFencedWorkOutcome.StaleLease) return;
                    if (outcome == EllaiFencedWorkOutcome.Committed) jobsCreated++;
                }
                finally
                {
                    if ((object)session is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                    else if ((object)session is IDisposable disposable) disposable.Dispose();
                    GC.KeepAlive(session);
                }
            }
            complete = await schedule.CompleteAsync(lease, ct).ConfigureAwait(false);
            logger.LogInformation("Ellai operational run {RunId} role {Role} finished {Outcome}; turns {WorkTurns}, jobs {JobsCreated}, model calls {ModelCalls}",
                runId, "ops.enrichment", complete ? "complete" : "stale-lease", workTurns, jobsCreated, 0);
        }
        finally
        {
            if (!complete)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await schedule.AbandonAsync(lease, timeout.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
        }
    }
}
