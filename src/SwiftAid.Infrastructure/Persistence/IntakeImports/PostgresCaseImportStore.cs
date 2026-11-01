using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Infrastructure.Persistence.IntakeImports;

public sealed class PostgresCaseImportStore(SwiftAidDbContext db, IDataProtectionProvider protection, TimeProvider clock) : ICaseImportStore
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    public async Task<CaseImportLease> OpenAsync(ActorContext actor, Guid importId, CaseImportContext context, long startRow, CancellationToken ct)
    {
        if (importId == Guid.Empty || startRow < 1) throw new CaseImportException("invalid-import");
        var now = clock.GetUtcNow();
        await EnsureScopeAsync(actor, context.WorkspaceId, context.Confirmations.OccurrenceCountryCode, now, ct);
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(context));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"case-import:" + importId.ToString("N")}, 0))", ct);
        var job = await db.Set<CaseImportEntity>().AsNoTracking().SingleOrDefaultAsync(row => row.ImportId == importId, ct);
        if (job is null)
        {
            if (startRow != 1) throw new CaseImportException("checkpoint-gap");
            job = new CaseImportEntity { ImportId = importId, ActorId = actor.ActorId, WorkspaceId = context.WorkspaceId,
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
        await EnsureScopeAsync(lease.Actor, lease.Context.WorkspaceId, lease.Context.Confirmations.OccurrenceCountryCode, now, ct);
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
        await EnsureScopeAsync(actor, job.WorkspaceId, job.CountryCode, now, ct);
        var counts = await db.Set<CaseImportRowEntity>().Where(row => row.ImportId == importId).GroupBy(row => row.State)
            .Select(group => new { State = group.Key, Count = group.LongCount() }).ToDictionaryAsync(row => row.State, row => row.Count, ct);
        var accepted = counts.GetValueOrDefault("Accepted") + counts.GetValueOrDefault("Replayed");
        var rejected = counts.GetValueOrDefault("Invalid") + counts.GetValueOrDefault("Denied") + counts.GetValueOrDefault("Conflict") + counts.GetValueOrDefault("Failed");
        return new(importId, job.State == "Running" && job.LeaseUntilUtc <= now ? "Paused" : job.State, job.NextRow,
            accepted, rejected, counts.GetValueOrDefault("RetryableFailure"), counts.GetValueOrDefault("Processing"), job.ErrorCode, job.UpdatedAtUtc);
    }

    private async Task<CaseImportEntity> LockAsync(CaseImportLease lease, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.Set<CaseImportEntity>().FromSqlInterpolated($"""
            SELECT * FROM swiftaid_ingestion.case_imports WHERE "ImportId" = {lease.ImportId}
            AND "ActorId" = {lease.Actor.ActorId} AND "LeaseId" = {lease.LeaseId} AND "LeaseUntilUtc" > {now} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct);
        return rows.SingleOrDefault() ?? throw new CaseImportException("lease-lost");
    }

    private async Task EnsureScopeAsync(ActorContext actor, string workspace, string country, DateTimeOffset now, CancellationToken ct)
    {
        var permitted = await (from state in db.ActorAccessStates
            join person in db.Actors on state.ActorId equals person.ActorId
            join grant in db.AccessGrantGroups on person.ActorId equals grant.ActorId
            join operation in db.AccessGrantOperations on grant.GrantGroupId equals operation.GrantGroupId
            join membership in db.OrganizationMemberships on grant.MembershipId equals membership.MembershipId
            join organization in db.Organizations on grant.OrganizationId equals organization.OrganizationId
            join scope in db.Workspaces on grant.WorkspaceId equals scope.WorkspaceId
            where person.ActorId == actor.ActorId && state.AccessEpoch == actor.AccessEpoch &&
                person.Status == PersistentActorStatus.Active && (person.ExpiresAtUtc == null || person.ExpiresAtUtc > now) &&
                scope.WorkspaceId == workspace && scope.OrganizationId == organization.OrganizationId &&
                scope.Status == PersistentResourceStatus.Active && organization.Status == PersistentResourceStatus.Active &&
                grant.CountryCode == country && grant.Status == PersistentGrantStatus.Active && grant.ValidFromUtc <= now &&
                grant.ExpiresAtUtc > now && operation.Operation == CaseOperation.ReadCase &&
                membership.ActorId == person.ActorId && membership.OrganizationId == organization.OrganizationId &&
                membership.Status == PersistentMembershipStatus.Active && membership.ValidFromUtc <= now &&
                (membership.ExpiresAtUtc == null || membership.ExpiresAtUtc > now)
            select person.ActorId).AnyAsync(ct);
        if (!permitted) throw new CaseImportException("not-authorized");
    }
}
