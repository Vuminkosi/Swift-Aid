using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Classification;
using SwiftAid.Contracts.Events;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Classification;
using SwiftAid.Infrastructure.Persistence.Processing;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.IntakeImports;

namespace SwiftAid.Infrastructure.Classification;

/// <summary>Gateway-owned, fenced classification persistence. No inference occurs inside SQL transactions.</summary>
public sealed class ClassificationRegistry(
    SwiftAidDbContext db, PostgresProtectedCaseEventPayloadStore payloads,
    IOptions<ClassificationOptions> configured, TimeProvider clock, IDataProtectionProvider protection,
    TranslationRegistry? translations = null)
{
    private ClassificationOptions Options => configured.Value;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ClassificationClaim> ClaimAsync(IngestionNotice notice, CancellationToken ct)
    {
        Options.Validate();
        if (!notice.IsValid() || notice.SourceKind != "case") throw new ArgumentException("Unsupported notice.");
        var noticeHash = ClassificationFingerprint.Hash(JsonSerializer.Serialize(notice, Json));
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync($"classification-inbox:{notice.MessageId}:{Options.Profile.Hash}", ct);
        var sourceNotice = await db.CaseEvents.AsNoTracking().SingleOrDefaultAsync(e => e.EventId == notice.MessageId, ct);
        if (sourceNotice is null || sourceNotice.CaseId != notice.SourceId || sourceNotice.AggregateVersion != notice.SourceVersion ||
            sourceNotice.ReportRevision != notice.ReportRevision || sourceNotice.EventType != notice.EventType || sourceNotice.RecordedAtUtc != notice.RecordedAtUtc)
            throw new ArgumentException("Notice does not match a committed source event.");
        var header = await db.ManagedCases.AsNoTracking().SingleAsync(c => c.CaseId == notice.SourceId, ct);
        await RequireScopeAsync(header.WorkspaceId, header.OrganizationId, ct);
        // Use the same organization/workspace-before-case lock order as case
        // commands. Hold the case revision stable only through admission; the
        // transaction ends before any provider inference starts.
        var lockedCases = await db.Database.SqlQuery<Guid>($"""
            SELECT case_id AS "Value" FROM managed_cases
            WHERE case_id = {header.CaseId} AND workspace_id = {header.WorkspaceId} AND organization_id = {header.OrganizationId}
            FOR SHARE
            """).ToArrayAsync(ct).ConfigureAwait(false);
        if (lockedCases.Length != 1) throw new UnauthorizedAccessException();
        header = await db.ManagedCases.AsNoTracking().SingleAsync(c => c.CaseId == notice.SourceId, ct);
        await SupersedeOldFactsAsync(header.CaseId, header.ReportRevision, ct);
        if (notice.ReportRevision < header.ReportRevision)
        {
            // Retain a durable terminal inbox record before acknowledging a stale
            // delivery. Do not decrypt source facts or admit model work for it.
            var configuration = Options.Profile.Hash;
            var obsolete = await db.Set<ClassificationInboxEntity>().FindAsync([notice.MessageId, configuration], ct);
            if (obsolete is not null && obsolete.NoticeHash != noticeHash)
                throw new ArgumentException("Changed message identity.");
            if (obsolete is null)
                db.Add(new ClassificationInboxEntity { MessageId = notice.MessageId, ConfigurationId = configuration,
                    NoticeHash = noticeHash, State = "Superseded", UpdatedAtUtc = clock.GetUtcNow() });
            else if (obsolete.State != "Superseded")
            {
                obsolete.State = "Superseded";
                obsolete.UpdatedAtUtc = clock.GetUtcNow();
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new ClassificationClaim("Superseded", ConfigurationHash: configuration);
        }
        if (notice.ReportRevision > header.ReportRevision)
            throw new InvalidDataException("The source revision is ahead of the case projection.");
        var source = await db.CaseEvents.AsNoTracking().Where(e => e.CaseId == header.CaseId && e.ReportRevision == notice.ReportRevision &&
            (e.EventType == "CaseReported" || e.EventType == "CaseReportCorrected")).OrderByDescending(e => e.AggregateVersion).FirstAsync(ct);
        var sourceLanguage = await ReadImportedLanguageAsync(source, header, ct);
        var profile = Options.Profile with { SourceLanguage = sourceLanguage ?? Options.Profile.SourceLanguage };
        // Read the immutable facts revision, even when this notice is an administrative event.
        var plaintext = await payloads.UnprotectForClassificationAsync(source, Options.Organizations, ct);
        string text;
        ReportQualityAssessment quality;
        try
        {
            var sourceEvent = CaseEventJournalReader.Read(source, plaintext);
            text = ExtractText(sourceEvent);
            var facts = sourceEvent switch
            {
                CaseReported reported => reported.ReportData,
                CaseReportCorrected corrected => corrected.CorrectedReportData,
                _ => throw new InvalidDataException()
            };
            quality = ReportQualityRules.Assess(facts, source.RecordedAtUtc);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        if (translations is not null && profile.SourceLanguage != "en")
        {
            var artifact = await translations.EnsureAsync(header, source, text, profile.SourceLanguage, ct);
            if (artifact is { State: "Completed", ReviewStatus: "Approved" })
            {
                text = translations.ReadTranslation(artifact);
                profile = profile with { SourceLanguage = "en", Translation = new(artifact.Id, artifact.Fingerprint,
                    artifact.SourceLanguage, artifact.ModelRevision, artifact.ReviewStatus, artifact.ReviewVersion) };
            }
        }
        var config = profile.Hash;
        var inbox = await db.Set<ClassificationInboxEntity>().FindAsync([notice.MessageId, config], ct);
        if (inbox is not null && inbox.NoticeHash != noticeHash) throw new ArgumentException("Changed message identity.");
        if (inbox?.State is "Completed" or "Failed" or "Superseded" &&
            await db.Set<ReportQualityEntity>().AnyAsync(q => q.SourceEventId == source.EventId && q.Version == ReportQualityRules.Version, ct))
        {
            // A completed abstention may acquire new translation work; persist admission before ACK.
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(header.ReportRevision == notice.ReportRevision ? inbox.State : "Superseded", FailureCode: inbox.FailureCode, ConfigurationHash: config);
        }
        inbox ??= new() { MessageId = notice.MessageId, ConfigurationId = config, NoticeHash = noticeHash, State = "Pending", UpdatedAtUtc = now };
        if (db.Entry(inbox).State == EntityState.Detached) db.Add(inbox);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO swiftaid_classification.quality_assessments ("SourceEventId", "Version", "CaseId", "ReportRevision", "AssessmentJson", "CreatedAtUtc")
            VALUES ({source.EventId}, {ReportQualityRules.Version}, {source.CaseId}, {source.ReportRevision}, {JsonSerializer.Serialize(quality)}::jsonb, {now})
            ON CONFLICT ("SourceEventId", "Version") DO NOTHING
            """, ct);
        if (text.Length > 32000) throw new ArgumentException("Classifiable input exceeds its source limit.");
        var fingerprint = ClassificationFingerprint.Create(header.OrganizationId, header.WorkspaceId, header.SensitivityCode, text, profile);
        await LockAsync($"classification-fingerprint:{fingerprint}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO swiftaid_classification.configurations (\"Id\", \"ProfileJson\", \"CreatedAtUtc\") VALUES ({config}, {JsonSerializer.Serialize(profile)}::jsonb, {now}) ON CONFLICT DO NOTHING", ct);
        var result = await db.Set<ClassificationResultEntity>().SingleOrDefaultAsync(r => r.Fingerprint == fingerprint, ct);
        if (result is not null) await LockAsync($"classification-result:{result.Id}", ct);
        // Refresh after waiting for a completion transaction.
        if (result is not null) await db.Entry(result).ReloadAsync(ct);
        // A contended inbox, fingerprint, or result lock may take longer than
        // a lease. Decide availability and issue its deadline using fresh time.
        now = clock.GetUtcNow();
        var created = result is null;
        result ??= new() { Id = Guid.NewGuid(), Fingerprint = fingerprint, OrganizationId = header.OrganizationId,
            WorkspaceId = header.WorkspaceId, ConfigurationId = config, ContentHash = ClassificationFingerprint.Hash(text),
            State = "Pending", CreatedAtUtc = now, AvailableAtUtc = now };
        if (created) db.Add(result);
        inbox.ResultId = result.Id;
        inbox.UpdatedAtUtc = now;
        var association = await db.Set<ClassificationAssociationEntity>().FindAsync([header.CaseId, notice.ReportRevision, config], ct);
        if (association is null)
        {
            db.Add(new ClassificationAssociationEntity { CaseId = header.CaseId, ReportRevision = notice.ReportRevision,
                ConfigurationId = config, SourceEventId = source.EventId, ResultId = result.Id,
                Reused = !created, CreatedAtUtc = now });
            AddEvent(result.Id, result.State == "Completed" ? "ClassificationReused" : "ClassificationRequested", header.CaseId, notice.ReportRevision);
        }
        else if (association.ResultId != result.Id) throw new InvalidOperationException("Immutable revision fingerprint changed.");

        ClassificationClaim response;
        if (result.State is "Completed" or "Failed")
        {
            inbox.State = header.ReportRevision == notice.ReportRevision ? result.State : "Superseded";
            inbox.FailureCode = result.FailureCode;
            response = new(inbox.State, FailureCode: result.FailureCode);
        }
        else if (result.LeaseUntilUtc > now || result.AvailableAtUtc > now) response = new("Busy");
        else if (result.Attempts >= Options.MaximumAttempts)
        {
            SetFailed(result, "lease-expired"); inbox.State = "Failed"; inbox.FailureCode = "lease-expired";
            AddEvent(result.Id, "ClassificationFailed"); response = new("Failed", FailureCode: "lease-expired");
        }
        else
        {
            result.State = "Leased"; result.Attempts++; result.LeaseId = Guid.NewGuid();
            result.AttemptStartedAtUtc = now;
            result.WorkerId = Options.WorkerId; result.LeaseUntilUtc = now.AddSeconds(Options.LeaseSeconds);
            result.FailureCode = null;
            response = new("Leased", new(result.Id, result.LeaseId.Value, config, fingerprint, result.LeaseUntilUtc.Value, text, profile));
        }
        await db.SaveChangesAsync(ct);
        // A historical backfill can create an old association after the initial sweep.
        await SupersedeOldFactsAsync(header.CaseId, header.ReportRevision, ct);
        await transaction.CommitAsync(ct);
        return response with { ConfigurationHash = config };
    }

    public async Task<string> CompleteAsync(ClassificationCompletion request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync($"classification-result:{request.ResultId}", ct);
        var result = await RequireResultAsync(request.ResultId, ct);
        if (result.ConfigurationId != request.ConfigurationHash || result.Fingerprint != request.Fingerprint)
            throw new ArgumentException("Result identity mismatch.");
        // Completion retry is harmless but only the exact original lease may reconcile it.
        if (result.State == "Completed")
        {
            var prior = JsonSerializer.Deserialize<ClassificationCompletion>(result.PredictionsJson!, Json);
            return prior == null || prior.LeaseId != request.LeaseId ||
                JsonSerializer.Serialize(prior, Json) != JsonSerializer.Serialize(request, Json) ? "Conflict" : "Completed";
        }
        if (!OwnsLease(result, request.LeaseId)) return "StaleLease";
        var configuration = await db.Set<ClassificationConfigurationEntity>().FindAsync([result.ConfigurationId], ct);
        var profile = JsonSerializer.Deserialize<ClassificationProfile>(configuration!.ProfileJson)!;
        if (profile.Translation is { } translated)
        {
            var artifact = translations is null ? null : await translations.LockedAsync(translated.ArtifactId, ct);
            if (artifact is null || translations?.Allows(artifact.OrganizationId) != true || artifact.ReviewStatus != "Approved" ||
                artifact.ReviewVersion != translated.ReviewVersion || artifact.Fingerprint != translated.Fingerprint)
            {
                SetFailed(result, "translation-review-changed");
                AddEvent(result.Id, "ClassificationFailed");
                await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return "Failed";
            }
        }
        ClassificationResultValidation.Validate(request, profile);
        result.State = "Completed"; result.CompletedAtUtc = clock.GetUtcNow(); result.PredictionsJson = JsonSerializer.Serialize(request, Json);
        result.AbstentionReason = request.AbstentionReason; result.InputTokens = request.InputTokens;
        result.InferenceMilliseconds = request.InferenceMilliseconds; ClearLease(result);
        AddEvent(result.Id, "ClassificationCompleted");
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE swiftaid_classification.inbox i SET "State" = CASE WHEN e.report_revision = c.report_revision THEN 'Completed' ELSE 'Superseded' END,
                "UpdatedAtUtc" = {clock.GetUtcNow()}
            FROM case_events e JOIN managed_cases c ON c.case_id = e.case_id
            WHERE i."MessageId" = e.event_id AND i."ResultId" = {result.Id}
            """, ct);
        await transaction.CommitAsync(ct);
        return "Completed";
    }

    public async Task<string> FailAsync(ClassificationFailure failure, CancellationToken ct)
    {
        if (failure.Code is not ("model-unavailable" or "inference-timeout" or "invalid-result" or "model-configuration"))
            throw new ArgumentException("Unknown failure code.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync($"classification-result:{failure.ResultId}", ct);
        var result = await RequireResultAsync(failure.ResultId, ct);
        if (!OwnsLease(result, failure.LeaseId)) return "StaleLease";
        var terminal = result.Attempts >= Options.MaximumAttempts || failure.Code == "model-configuration";
        result.State = terminal ? "Failed" : "RetryScheduled"; result.FailureCode = failure.Code;
        result.AvailableAtUtc = clock.GetUtcNow().AddSeconds(Math.Min(300, 1 << result.Attempts)); ClearLease(result);
        if (terminal) AddEvent(result.Id, "ClassificationFailed");
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return result.State;
    }

    public async Task<string> RenewAsync(ClassificationRenewal renewal, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync($"classification-result:{renewal.ResultId}", ct);
        var result = await RequireResultAsync(renewal.ResultId, ct);
        if (!OwnsLease(result, renewal.LeaseId)) return "StaleLease";
        // One attempt has a hard deadline, even if a hung worker keeps heartbeating.
        if (result.AttemptStartedAtUtc is null || clock.GetUtcNow() - result.AttemptStartedAtUtc >= TimeSpan.FromMinutes(10)) return "StaleLease";
        result.LeaseUntilUtc = clock.GetUtcNow().AddSeconds(Options.LeaseSeconds);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return "Leased";
    }

    public async Task<string> QuarantineAsync(ClassificationQuarantine request, CancellationToken ct)
    {
        if (request.StreamSequence == 0 || request.PayloadSha256.Length != 64 || !request.PayloadSha256.All(char.IsAsciiHexDigit) ||
            request.Reason is not ("invalid-notice" or "unsupported-schema")) throw new ArgumentException("Invalid quarantine metadata.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync($"classification-quarantine:{Options.WorkerId}:{request.StreamSequence}", ct);
        var row = await db.Set<ClassificationQuarantineEntity>().FindAsync([Options.WorkerId, request.StreamSequence], ct);
        if (row is not null && row.PayloadSha256 != request.PayloadSha256) return "Conflict";
        if (row is null) db.Add(new ClassificationQuarantineEntity { WorkerId = Options.WorkerId, StreamSequence = request.StreamSequence,
            PayloadSha256 = request.PayloadSha256, Reason = request.Reason, RecordedAtUtc = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return "Quarantined";
    }

    private async Task<ClassificationResultEntity> RequireResultAsync(Guid id, CancellationToken ct)
    {
        var result = await db.Set<ClassificationResultEntity>().SingleAsync(r => r.Id == id, ct);
        await RequireScopeAsync(result.WorkspaceId, result.OrganizationId, ct);
        return result;
    }

    private async Task RequireScopeAsync(string workspace, string organization, CancellationToken ct)
    {
        if (!Options.Organizations.Contains(organization, StringComparer.Ordinal) ||
            !await db.Database.SqlQuery<bool>($"SELECT swiftaid_internal.lock_workspace_organization({workspace}, {organization}) AS \"Value\"").SingleAsync(ct))
            throw new UnauthorizedAccessException();
    }

    private Task<int> LockAsync(string key, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    private Task<int> SupersedeOldFactsAsync(Guid caseId, long currentRevision, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO swiftaid_classification.outbox ("Id", "ResultId", "CaseId", "ReportRevision", "EventType", "CreatedAtUtc", "AvailableAtUtc", "Attempts")
            SELECT gen_random_uuid(), a."ResultId", a."CaseId", a."ReportRevision", 'ClassificationSuperseded', {now}, {now}, 0
            FROM swiftaid_classification.associations a
            WHERE a."CaseId" = {caseId} AND a."ReportRevision" < {currentRevision}
            ON CONFLICT ("ResultId", "CaseId", "ReportRevision", "EventType") WHERE "EventType" = 'ClassificationSuperseded' DO NOTHING
            """, ct);
    }
    private bool OwnsLease(ClassificationResultEntity r, Guid lease) =>
        r.State == "Leased" && r.LeaseId == lease && r.WorkerId == Options.WorkerId && r.LeaseUntilUtc > clock.GetUtcNow();
    private static void ClearLease(ClassificationResultEntity r) { r.LeaseId = null; r.LeaseUntilUtc = null; r.WorkerId = null; }
    private static void SetFailed(ClassificationResultEntity r, string code) { r.State = "Failed"; r.FailureCode = code; ClearLease(r); }
    private void AddEvent(Guid result, string type, Guid? caseId = null, long? revision = null) => db.Add(new ClassificationEventEntity
    { Id = Guid.NewGuid(), ResultId = result, CaseId = caseId, ReportRevision = revision,
        EventType = type, CreatedAtUtc = clock.GetUtcNow(), AvailableAtUtc = clock.GetUtcNow() });

    private static string ExtractText(CaseEvent source)
    {
        var data = source switch { CaseReported r => r.ReportData, CaseReportCorrected r => r.CorrectedReportData, _ => throw new InvalidDataException() };
        var text = data switch
        {
            CaseReportEventData.HumanV2 h => EvidenceText(h.Report.Evidence),
            CaseReportEventData.AnimalV2 a => EvidenceText(a.Report.Evidence),
            CaseReportEventData.LegacyV1 l => string.Join("\n", new[] { l.Report.Narrative }.Concat(l.Report.Observations.Select(o => o.Narrative))),
            _ => throw new InvalidDataException()
        };
        return ClassificationFingerprint.Normalize(text);
    }
    private static string EvidenceText(ReportEvidence evidence) => string.Join("\n",
        new[] { evidence.Narrative?.Text ?? "" }.Concat(evidence.Observations.Select(o => o.Text)));

    private async Task<string?> ReadImportedLanguageAsync(CaseEventEntity source, ManagedCaseEntity header, CancellationToken ct)
    {
        // Only the accepted source row for this exact immutable command supplies language.
        // Later corrections and unrelated imports cannot borrow its language or plaintext.
        var rows = await (from row in db.Set<CaseImportRowEntity>().AsNoTracking()
                          join job in db.Set<CaseImportEntity>().AsNoTracking() on row.ImportId equals job.ImportId
                          where row.CaseId == source.CaseId && row.CommandId == source.CommandId && job.ActorId == source.ActorId &&
                              job.WorkspaceId == header.WorkspaceId && (job.OrganizationId == null || job.OrganizationId == header.OrganizationId) &&
                              (row.State == "Accepted" || row.State == "Replayed")
                          orderby job.CreatedAtUtc, row.RowNumber
                          select new { row.ImportId, row.RowNumber, row.ProtectedSource, row.SourceHash, job.ActorId }).Take(1).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var item = rows[0];
        var plaintext = protection.CreateProtector("SwiftAid.CaseImport.Row.v1", item.ActorId, item.ImportId.ToString("N"),
            item.RowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)).Unprotect(item.ProtectedSource);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(plaintext), item.SourceHash)) throw new InvalidDataException("Invalid import provenance.");
            using var document = JsonDocument.Parse(plaintext);
            var columns = document.RootElement.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToArray();
            var index = Array.IndexOf(columns, "source_language");
            if (index < 0) return "und";
            var language = document.RootElement.GetProperty("values")[index].GetString()?.Trim().ToLowerInvariant().Split('-')[0];
            return language is { Length: 2 or 3 } && language.All(char.IsAsciiLetterLower) ? language : "und";
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
