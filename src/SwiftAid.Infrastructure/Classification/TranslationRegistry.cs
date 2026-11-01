using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Classification;
using SwiftAid.Application.Ellai;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Classification;
using SwiftAid.Infrastructure.Persistence.Entities.Cases;

namespace SwiftAid.Infrastructure.Classification;

public sealed record TranslationWork(Guid ArtifactId, Guid LeaseId, string SourceLanguage, string Text);

/// <summary>Authoritative protected artifacts. No database transaction spans provider inference.</summary>
public sealed class TranslationRegistry(SwiftAidDbContext db, IDataProtectionProvider protection,
    IOptions<TranslationOptions> configured, IHostEnvironment environment, TimeProvider clock)
{
    public bool Allows(string organization) => configured.Value.Allows(organization, environment.EnvironmentName);

    public async Task<TranslationArtifactEntity?> EnsureAsync(ManagedCaseEntity header, CaseEventEntity source,
        string text, string sourceLanguage, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Translation admission requires its source transaction.");
        if (source.CaseId != header.CaseId || source.ReportRevision < 1) throw new InvalidDataException("Invalid translation source association.");
        var language = TranslationIdentity.Language(sourceLanguage);
        if (!Allows(header.OrganizationId) || language is null || string.IsNullOrWhiteSpace(text) || text.Length > 6000) return null;
        var normalized = ClassificationFingerprint.Normalize(text);
        var fingerprint = TranslationIdentity.Fingerprint(header.OrganizationId, header.WorkspaceId, header.SensitivityCode, normalized, language);
        await LockAsync("translation-fingerprint:" + fingerprint, ct);
        var artifact = await db.Set<TranslationArtifactEntity>().SingleOrDefaultAsync(x => x.Fingerprint == fingerprint, ct);
        if (artifact is null)
        {
            artifact = new() { Id = Guid.NewGuid(), OrganizationId = header.OrganizationId, WorkspaceId = header.WorkspaceId,
                Fingerprint = fingerprint, ContentHash = ClassificationFingerprint.Hash(normalized), SourceLanguage = language,
                ModelRevision = TranslationIdentity.Revision, Configuration = TranslationIdentity.Configuration,
                ProtectedSource = [], AvailableAtUtc = clock.GetUtcNow(), CreatedAtUtc = clock.GetUtcNow() };
            artifact.ProtectedSource = Protect(artifact, normalized, "source");
            db.Add(artifact);
        }
        if (!await db.Set<TranslationAssociationEntity>().AnyAsync(x => x.SourceEventId == source.EventId && x.ArtifactId == artifact.Id, ct))
            db.Add(new TranslationAssociationEntity { SourceEventId = source.EventId, ArtifactId = artifact.Id,
                CaseId = header.CaseId, ReportRevision = source.ReportRevision });
        return artifact;
    }

    public string ReadTranslation(TranslationArtifactEntity artifact) =>
        Unprotect(artifact, artifact.ProtectedTranslation ?? throw new InvalidDataException("Missing translation artifact."), "translation");
    public string ReadSource(TranslationArtifactEntity artifact) => Unprotect(artifact, artifact.ProtectedSource, "source");

    public async Task<TranslationWork?> ClaimAsync(CancellationToken ct)
    {
        if (!configured.Value.Enabled || environment.EnvironmentName is not ("Development" or "Test")) return null;
        var organizations = configured.Value.Organizations.Where(Allows).ToArray();
        if (organizations.Length == 0) return null;
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<TranslationArtifactEntity>().FromSqlInterpolated($"""
            SELECT * FROM swiftaid_translation.artifacts WHERE "OrganizationId" = ANY({organizations})
            AND "State" IN ('Pending','RetryScheduled','Leased') AND "AvailableAtUtc" <= {now}
            AND ("LeaseUntilUtc" IS NULL OR "LeaseUntilUtc" <= {now})
            ORDER BY "AvailableAtUtc", "CreatedAtUtc" LIMIT 1 FOR UPDATE SKIP LOCKED
            """).SingleOrDefaultAsync(ct);
        if (row is null) return null;
        if (row.Attempts >= 5)
        {
            row.State = "Failed"; row.FailureCode = "lease-expired"; row.LeaseId = null; row.LeaseUntilUtc = null;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null;
        }
        if (!await db.Database.SqlQuery<bool>($"SELECT swiftaid_internal.lock_workspace_organization({row.WorkspaceId}, {row.OrganizationId}) AS \"Value\"").SingleAsync(ct))
        {
            row.State = "Cancelled"; row.FailureCode = "scope-unavailable"; row.LeaseId = null; row.LeaseUntilUtc = null;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null;
        }
        string text;
        try
        {
            text = Unprotect(row, row.ProtectedSource, "source");
            if (ClassificationFingerprint.Hash(text) != row.ContentHash) throw new InvalidDataException("Invalid protected source.");
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException)
        {
            row.State = "Failed"; row.FailureCode = "invalid-protected-source"; row.LeaseId = null; row.LeaseUntilUtc = null;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null;
        }
        row.State = "Leased"; row.LeaseId = Guid.NewGuid(); row.LeaseUntilUtc = now.AddMinutes(3); row.Attempts++;
        row.FailureCode = null;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(row.Id, row.LeaseId.Value, row.SourceLanguage, text);
    }

    public async Task<string> CompleteAsync(TranslationWork work, EllaiTranslationResult result, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await LockedAsync(work.ArtifactId, ct);
        var resultHash = ClassificationFingerprint.Hash(JsonSerializer.Serialize(result));
        if (row.State == "Completed")
            return row.CompletedLeaseId == work.LeaseId && row.ResultHash == resultHash ? "Completed" : "Conflict";
        if (!Owns(row, work.LeaseId)) return "StaleLease";
        if (!Allows(row.OrganizationId)) return "Paused";
        if (!await db.Database.SqlQuery<bool>($"SELECT swiftaid_internal.lock_workspace_organization({row.WorkspaceId}, {row.OrganizationId}) AS \"Value\"").SingleAsync(ct))
        {
            row.State = "Cancelled"; row.FailureCode = "scope-unavailable"; row.LeaseId = null; row.LeaseUntilUtc = null;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return row.State;
        }
        if (result.RequestId != work.ArtifactId.ToString("N") || result.SourceLanguage != row.SourceLanguage ||
            result.TargetLanguage != "eng" || result.ModelId != TranslationIdentity.Model || result.ModelRevision != row.ModelRevision ||
            result.Quality != "unreviewed" || string.IsNullOrWhiteSpace(result.Text) || result.Text.Length > 16000 || result.InputTokens is < 1 or > 512 ||
            !double.IsFinite(result.InferenceMilliseconds) || result.InferenceMilliseconds < 0)
            throw new InvalidDataException("Invalid translation completion.");
        row.ProtectedTranslation = Protect(row, result.Text, "translation");
        row.FindingsJson = JsonSerializer.Serialize(TranslationIdentity.Assess(work.Text, result.Text));
        row.ReviewStatus = "Unreviewed"; row.State = "Completed"; row.CompletedAtUtc = clock.GetUtcNow();
        row.InferenceMilliseconds = result.InferenceMilliseconds; row.LeaseId = null; row.LeaseUntilUtc = null;
        row.CompletedLeaseId = work.LeaseId; row.ResultHash = resultHash;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return row.State;
    }

    public async Task<string> FailAsync(TranslationWork work, bool retryable, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await LockedAsync(work.ArtifactId, ct);
        if (!Owns(row, work.LeaseId)) return "StaleLease";
        row.State = retryable && row.Attempts < 5 ? "RetryScheduled" : "Failed";
        row.FailureCode = retryable ? "provider-unavailable" : "invalid-provider-result";
        row.AvailableAtUtc = clock.GetUtcNow().AddSeconds(Math.Min(300, 1 << row.Attempts));
        row.LeaseId = null; row.LeaseUntilUtc = null;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return row.State;
    }

    public async Task<TranslationArtifactEntity> LockedAsync(Guid id, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Translation lock requires a transaction.");
        var row = await db.Set<TranslationArtifactEntity>().FromSqlInterpolated(
            $"SELECT * FROM swiftaid_translation.artifacts WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
        await db.Entry(row).ReloadAsync(ct);
        return row;
    }

    private bool Owns(TranslationArtifactEntity row, Guid lease) => row.State == "Leased" && row.LeaseId == lease && row.LeaseUntilUtc > clock.GetUtcNow();
    private Task<int> LockAsync(string key, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    private IDataProtector Protector(TranslationArtifactEntity row, string purpose) => protection.CreateProtector(
        "SwiftAid.Translation.v1", row.OrganizationId, row.WorkspaceId, row.Id.ToString("N"), row.Fingerprint, purpose);
    private byte[] Protect(TranslationArtifactEntity row, string value, string purpose)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return Protector(row, purpose).Protect(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private string Unprotect(TranslationArtifactEntity row, byte[] value, string purpose)
    {
        var bytes = Protector(row, purpose).Unprotect(value);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
