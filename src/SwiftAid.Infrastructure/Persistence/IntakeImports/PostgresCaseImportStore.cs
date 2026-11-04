using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Infrastructure.Persistence.IntakeImports;

public sealed class PostgresCaseImportStore(SwiftAidDbContext db, IDataProtectionProvider protection, TimeProvider clock) : ICaseImportStore, ICaseImportWorkspaceReader
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    public async Task<CaseImportLease> OpenAsync(ActorContext actor, Guid importId, CaseImportContext context, long startRow, CancellationToken ct)
    {
        if (importId == Guid.Empty || startRow < 1) throw new CaseImportException("invalid-import");
        var now = clock.GetUtcNow();
        await EnsureScopeAsync(actor, context.WorkspaceId, context.Confirmations.OccurrenceCountryCode, now, ct, context.OrganizationId);
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(context));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"case-import:" + importId.ToString("N")}, 0))", ct);
        var job = await db.Set<CaseImportEntity>().AsNoTracking().SingleOrDefaultAsync(row => row.ImportId == importId, ct);
        if (job is null)
        {
            if (startRow != 1) throw new CaseImportException("checkpoint-gap");
            job = new CaseImportEntity { ImportId = importId, ActorId = actor.ActorId, WorkspaceId = context.WorkspaceId,
                OrganizationId = context.OrganizationId,
                CountryCode = context.Confirmations.OccurrenceCountryCode, ContextHash = hash, State = "Running", CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Add(job);
        }
        else
        {
            if (job.ActorId != actor.ActorId) throw new CaseImportException("not-authorized");
            if (!CryptographicOperations.FixedTimeEquals(job.ContextHash, hash)) throw new CaseImportException("import-context-conflict");
            if (job.LeaseUntilUtc > now) throw new CaseImportException("import-busy");
            if (startRow > job.NextRow) throw new CaseImportException("checkpoint-gap");
            if (job.FinalRow is long final && startRow > final) throw new CaseImportException("import-completed");
            db.Attach(job);
        }
        job.LeaseId = Guid.NewGuid();
        job.LeaseUntilUtc = now + LeaseDuration;
        job.State = "Running";
        job.UpdatedAtUtc = now;
        job.ErrorCode = null;
        var lease = new CaseImportLease(importId, job.LeaseId.Value, actor, context);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.Entry(job).State = EntityState.Detached;
        return lease;
    }

    public async Task<int> BeginRowAsync(CaseImportLease lease, long rowNumber, Guid? commandId, byte[] sourceHash, byte[] sourceBytes, CancellationToken ct)
    {
        if (sourceHash.Length != 32 || sourceBytes.Length > 400_000) throw new CaseImportException("row-too-large");
        var now = clock.GetUtcNow();
        await EnsureScopeAsync(lease.Actor, lease.Context.WorkspaceId, lease.Context.Confirmations.OccurrenceCountryCode, now, ct, lease.Context.OrganizationId);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockAsync(lease, now, ct);
        if (rowNumber > job.NextRow || rowNumber < 1 || job.FinalRow is long final && rowNumber > final)
            throw new CaseImportException("checkpoint-gap");
        var row = await db.Set<CaseImportRowEntity>().AsNoTracking().SingleOrDefaultAsync(item => item.ImportId == lease.ImportId && item.RowNumber == rowNumber, ct);
        if (row is null)
        {
            row = new CaseImportRowEntity { ImportId = lease.ImportId, RowNumber = rowNumber, CommandId = commandId,
                SourceHash = sourceHash, ProtectedSource = protection.CreateProtector("SwiftAid.CaseImport.Row.v1", lease.Actor.ActorId,
                    lease.ImportId.ToString("N"), rowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)).Protect(sourceBytes),
                State = "Processing", UpdatedAtUtc = now };
            db.Add(row);
        }
        else
        {
            if (!CryptographicOperations.FixedTimeEquals(row.SourceHash, sourceHash)) throw new CaseImportException("row-content-conflict");
            db.Attach(row);
        }
        db.Attach(job);
        row.Attempts++;
        if (row.CaseId is null) row.State = "Processing";
        row.UpdatedAtUtc = now;
        job.LeaseUntilUtc = now + LeaseDuration;
        job.UpdatedAtUtc = now;
        var attempts = row.Attempts;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.Entry(row).State = EntityState.Detached;
        db.Entry(job).State = EntityState.Detached;
        return attempts;
    }

    public async Task CompleteRowAsync(CaseImportLease lease, CaseImportRowOutcome result, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await LockAsync(lease, now, ct);
        db.Attach(job);
        var row = await db.Set<CaseImportRowEntity>().SingleAsync(item => item.ImportId == lease.ImportId && item.RowNumber == result.RowNumber, ct);
        if (row.CommandId != result.CommandId) throw new CaseImportException("row-identity-conflict");
        // A denied replay cannot erase the historical fact that this row already committed a case.
        if (row.CaseId is null || result.Status is "Accepted" or "Replayed") row.State = result.Status;
        row.CaseId = result.CaseId ?? row.CaseId;
        row.ErrorCode = result.ErrorCode;
        row.UpdatedAtUtc = now;
        if (result.Status != "RetryableFailure") job.NextRow = Math.Max(job.NextRow, result.RowNumber + 1);
        job.LeaseUntilUtc = now + LeaseDuration;
        job.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.Entry(row).State = EntityState.Detached;
        db.Entry(job).State = EntityState.Detached;
    }

    public async Task CloseAsync(CaseImportLease lease, string state, string? errorCode, CancellationToken ct)
    {
        if (state is not ("Paused" or "Completed" or "Failed")) throw new ArgumentException("Invalid import state.");
        var now = clock.GetUtcNow();
        await db.Set<CaseImportEntity>().Where(row => row.ImportId == lease.ImportId && row.ActorId == lease.Actor.ActorId &&
                row.LeaseId == lease.LeaseId && row.LeaseUntilUtc > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.State, row => row.FinalRow != null ? "Completed" : state)
                .SetProperty(row => row.FinalRow, row => state == "Completed" ? row.NextRow - 1 : row.FinalRow)
                .SetProperty(row => row.LeaseId, (Guid?)null).SetProperty(row => row.LeaseUntilUtc, (DateTimeOffset?)null)
                .SetProperty(row => row.ErrorCode, errorCode).SetProperty(row => row.UpdatedAtUtc, now), ct);
    }

    public async Task<CaseImportSummary?> GetAsync(ActorContext actor, Guid importId, CancellationToken ct)
    {
        var job = await db.Set<CaseImportEntity>().AsNoTracking().SingleOrDefaultAsync(row => row.ImportId == importId && row.ActorId == actor.ActorId, ct);
        if (job is null) return null;
        var now = clock.GetUtcNow();
        await EnsureScopeAsync(actor, job.WorkspaceId, job.CountryCode, now, ct, job.OrganizationId);
        var counts = await db.Set<CaseImportRowEntity>().Where(row => row.ImportId == importId).GroupBy(row => row.State)
            .Select(group => new { State = group.Key, Count = group.LongCount() }).ToDictionaryAsync(row => row.State, row => row.Count, ct);
        var accepted = counts.GetValueOrDefault("Accepted") + counts.GetValueOrDefault("Replayed");
        var rejected = counts.GetValueOrDefault("Invalid") + counts.GetValueOrDefault("Denied") + counts.GetValueOrDefault("Conflict") + counts.GetValueOrDefault("Failed");
        return new(importId, job.State == "Running" && job.LeaseUntilUtc <= now ? "Paused" : job.State, job.NextRow,
            accepted, rejected, counts.GetValueOrDefault("RetryableFailure"), counts.GetValueOrDefault("Processing"), job.ErrorCode, job.UpdatedAtUtc, job.OrganizationId);
    }

    public async Task<bool?> MatchesContextAsync(ActorContext actor, Guid importId, CaseImportContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        CaseCsvImportService.ValidateContext(context);
        if (importId == Guid.Empty) throw new CaseImportException("invalid-import");
        var job = await db.Set<CaseImportEntity>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.ImportId == importId && row.ActorId == actor.ActorId, ct);
        if (job is null) return null;
        await EnsureScopeAsync(actor, job.WorkspaceId, job.CountryCode, clock.GetUtcNow(), ct, job.OrganizationId);
        var proposed = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(context));
        try { return CryptographicOperations.FixedTimeEquals(job.ContextHash, proposed); }
        finally { CryptographicOperations.ZeroMemory(proposed); }
    }

    private async Task<CaseImportEntity> LockAsync(CaseImportLease lease, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.Set<CaseImportEntity>().FromSqlInterpolated($"""
            SELECT * FROM swiftaid_ingestion.case_imports WHERE "ImportId" = {lease.ImportId}
            AND "ActorId" = {lease.Actor.ActorId} AND "LeaseId" = {lease.LeaseId} AND "LeaseUntilUtc" > {now} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct);
        return rows.SingleOrDefault() ?? throw new CaseImportException("lease-lost");
    }

    private async Task EnsureScopeAsync(ActorContext actor, string workspace, string country, DateTimeOffset now, CancellationToken ct, string? organizationId = null)
    {
        if (!await AllowedWorkspaces(actor, now).AnyAsync(scope => scope.WorkspaceId == workspace && scope.CountryCode == country &&
            (organizationId != null ? scope.OrganizationId == organizationId : db.Workspaces.Any(original => original.WorkspaceId == workspace && original.OrganizationId == scope.OrganizationId)), ct))
            throw new CaseImportException("not-authorized");
    }

    public async Task<IReadOnlyList<CaseImportWorkspace>> GetWorkspacesAsync(ActorContext actor, CancellationToken ct) =>
        await AllowedWorkspaces(actor, clock.GetUtcNow()).Distinct().OrderBy(scope => scope.WorkspaceId)
            .ThenBy(scope => scope.OrganizationId).ThenBy(scope => scope.CountryCode).Take(100)
            .Select(scope => new CaseImportWorkspace(scope.WorkspaceId, scope.OrganizationId, scope.CountryCode)).ToArrayAsync(ct);

    public async Task<IReadOnlyList<CaseImportHistoryItem>> GetRecentAsync(ActorContext actor, string workspaceId, string countryCode, CancellationToken ct, string? organizationId = null)
    {
        var now = clock.GetUtcNow();
        await EnsureScopeAsync(actor, workspaceId, countryCode, now, ct, organizationId);
        organizationId ??= await db.Workspaces.Where(workspace => workspace.WorkspaceId == workspaceId).Select(workspace => workspace.OrganizationId).SingleAsync(ct);
        return await db.Set<CaseImportEntity>().AsNoTracking()
            .Where(job => job.ActorId == actor.ActorId && job.WorkspaceId == workspaceId && job.CountryCode == countryCode &&
                (job.OrganizationId == organizationId || job.OrganizationId == null && db.Workspaces.Any(original => original.WorkspaceId == workspaceId && original.OrganizationId == organizationId)))
            .OrderByDescending(job => job.UpdatedAtUtc).ThenBy(job => job.ImportId).Take(20)
            .Select(job => new CaseImportHistoryItem(job.ImportId, job.WorkspaceId, job.CountryCode,
                job.State == "Running" && job.LeaseUntilUtc <= now ? "Paused" : job.State, job.UpdatedAtUtc)).ToArrayAsync(ct);
    }

    private sealed class WorkspaceScope
    {
        public required string WorkspaceId { get; init; }
        public required string OrganizationId { get; init; }
        public required string CountryCode { get; init; }
    }

    public async Task<IReadOnlyList<CaseImportRowOutcome>> GetIssuesAsync(ActorContext actor, Guid importId, long afterRow, CancellationToken ct)
    {
        if (afterRow < 0) throw new CaseImportException("invalid-cursor");
        // Reuse owner and current-scope authorization before returning even sanitized row metadata.
        if (await GetAsync(actor, importId, ct) is null) return [];
        return await db.Set<CaseImportRowEntity>().AsNoTracking()
            .Where(row => row.ImportId == importId && row.RowNumber > afterRow && row.State != "Accepted" && row.State != "Replayed")
            .OrderBy(row => row.RowNumber).Take(20)
            .Select(row => new CaseImportRowOutcome(row.RowNumber, row.CommandId, row.State, row.CaseId, row.ErrorCode)).ToArrayAsync(ct);
    }

    private IQueryable<WorkspaceScope> AllowedWorkspaces(ActorContext actor, DateTimeOffset now) =>
        from state in db.ActorAccessStates
            join person in db.Actors on state.ActorId equals person.ActorId
            join grant in db.AccessGrantGroups on person.ActorId equals grant.ActorId
            join operation in db.AccessGrantOperations on grant.GrantGroupId equals operation.GrantGroupId
            join membership in db.OrganizationMemberships on grant.MembershipId equals membership.MembershipId
            join organization in db.Organizations on grant.OrganizationId equals organization.OrganizationId
            join scope in db.Workspaces on grant.WorkspaceId equals scope.WorkspaceId
            where person.ActorId == actor.ActorId && state.AccessEpoch == actor.AccessEpoch &&
                person.Status == PersistentActorStatus.Active && (person.ExpiresAtUtc == null || person.ExpiresAtUtc > now) &&
                db.WorkspaceOrganizations.Any(link => link.WorkspaceId == scope.WorkspaceId &&
                    link.OrganizationId == organization.OrganizationId && link.Status == PersistentResourceStatus.Active) &&
                scope.Status == PersistentResourceStatus.Active && organization.Status == PersistentResourceStatus.Active &&
                grant.Status == PersistentGrantStatus.Active && grant.ValidFromUtc <= now &&
                grant.ExpiresAtUtc > now && operation.Operation == CaseOperation.ReadCase &&
                membership.ActorId == person.ActorId && membership.OrganizationId == organization.OrganizationId &&
                membership.Status == PersistentMembershipStatus.Active && membership.ValidFromUtc <= now &&
                (membership.ExpiresAtUtc == null || membership.ExpiresAtUtc > now)
            select new WorkspaceScope { WorkspaceId = scope.WorkspaceId, OrganizationId = organization.OrganizationId, CountryCode = grant.CountryCode };
}
