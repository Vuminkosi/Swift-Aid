using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Classification;
using SwiftAid.Application.Surveillance;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Classification;
using SwiftAid.Infrastructure.Persistence.Processing;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Surveillance;

namespace SwiftAid.Infrastructure.Surveillance;

/// <summary>
/// Independent bounded consumer of committed source revisions and classification state.
/// Work receipts and derived evidence commit together. No sequence watermark can skip a late commit.
/// </summary>
public sealed class SurveillanceProcessor(SwiftAidDbContext db, PostgresProtectedCaseEventPayloadStore payloads,
    IOptions<SurveillanceOptions> options, IOptions<ClassificationOptions> classification, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        var settings = options.Value;
        settings.Validate();
        if (!settings.Enabled) return false;
        var profile = classification.Value.Profile with { SourceLanguage = "en" };
        var config = profile.Hash;
        var now = clock.GetUtcNow();
        // A new facts revision or classifier outcome makes a new receipt identity. Failed inference
        // does not block deterministic time/location admission or reporting-activity accounting.
        var next = await db.Database.SqlQuery<SourceWork>($"""
            SELECT q.* FROM (
                SELECT md5(e.event_id::text || ':' || c.resource_version::text || ':' || {SurveillanceDetector.Version} || ':' ||
                    {config} || ':' || COALESCE(r."Id"::text, '') || ':' || COALESCE(r."State", 'Absent')) AS "Id",
                    c.case_id AS "CaseId", e.event_id AS "SourceEventId", c.resource_version AS "ResourceVersion",
                    r."Id" AS "ResultId", c.workspace_id AS "WorkspaceId", c.organization_id AS "OrganizationId"
                FROM managed_cases c JOIN case_events e ON e.case_id = c.case_id AND e.report_revision = c.report_revision
                    AND e.event_type IN ('CaseReported','CaseReportCorrected')
                LEFT JOIN swiftaid_classification.associations a ON a."CaseId" = c.case_id
                    AND a."ReportRevision" = c.report_revision AND a."ConfigurationId" = {config}
                LEFT JOIN swiftaid_classification.results r ON r."Id" = a."ResultId"
                WHERE c.workspace_id = ANY({settings.Workspaces}) AND c.organization_id = ANY({settings.Organizations})
            ) q LEFT JOIN swiftaid_surveillance.work w ON w."Id" = q."Id"
            WHERE w."Id" IS NULL OR (w."State" = 'RetryScheduled' AND w."AvailableAtUtc" <= {now} AND w."Attempts" < 5)
            ORDER BY q."SourceEventId" LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (next is null) return false;
        try
        {
            await ProcessAsync(next, profile, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            db.ChangeTracker.Clear();
            var failure = error is SurveillanceCapacityException ? "scope-capacity-exceeded" :
                error is UnauthorizedAccessException ? "scope-unavailable" : "processing-unavailable";
            // The processing transaction was rolled back. Persist an observable bounded failure separately.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO swiftaid_surveillance.work ("Id", "CaseId", "State", "Attempts", "AvailableAtUtc", "FailureCode")
                VALUES ({next.Id}, {next.CaseId}, 'RetryScheduled', 1, {now.AddSeconds(5)}, {failure})
                ON CONFLICT ("Id") DO UPDATE SET "Attempts" = LEAST(5, swiftaid_surveillance.work."Attempts" + 1),
                    "State" = CASE WHEN swiftaid_surveillance.work."Attempts" >= 4 THEN 'Failed' ELSE 'RetryScheduled' END,
                    "AvailableAtUtc" = {now.AddSeconds(30)}, "FailureCode" = {failure}
                WHERE swiftaid_surveillance.work."State" NOT IN ('Completed','Failed')
                """, ct);
        }
        return true;
    }

    private async Task ProcessAsync(SourceWork source, ClassificationProfile profile, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Separate workspaces may run on different replicas; one workspace's recomputations serialize.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"surveillance:" + source.WorkspaceId + ":" + source.OrganizationId}, 0))", ct);
        var work = await db.Set<SurveillanceWorkEntity>().FindAsync([source.Id], ct);
        if (work?.State is "Completed" or "Failed") return;
        var header = await db.ManagedCases.FromSqlInterpolated($"SELECT * FROM managed_cases WHERE case_id = {source.CaseId} FOR SHARE").SingleAsync(ct);
        if (header.ResourceVersion != source.ResourceVersion) return;
        if (!await db.Database.SqlQuery<bool>($"SELECT swiftaid_internal.lock_workspace_organization({source.WorkspaceId}, {source.OrganizationId}) AS \"Value\"").SingleAsync(ct))
            throw new UnauthorizedAccessException();
        work ??= new() { Id = source.Id, CaseId = source.CaseId, State = "Pending" };
        if (db.Entry(work).State == EntityState.Detached) db.Add(work);
        var original = await db.Set<SurveillanceInputEntity>().FindAsync([source.CaseId], ct);
        var previous = original is null ? null : JsonSerializer.Deserialize<SurveillanceObservation>(original.ObservationJson, Json);
        var observation = await ReadObservationAsync(source, header, profile, ct);
        var input = original ?? new SurveillanceInputEntity { CaseId = header.CaseId, WorkspaceId = header.WorkspaceId,
            OrganizationId = header.OrganizationId, WorkId = source.Id, ObservationJson = "{}" };
        input.WorkId = source.Id;
        input.ObservationJson = JsonSerializer.Serialize(observation, Json);
        if (original is null) db.Add(input);
        // Persist within this transaction so queries see the replacement; failures roll it back.
        await db.SaveChangesAsync(ct);
        var rows = await db.Set<SurveillanceInputEntity>().AsNoTracking()
            .Where(i => i.WorkspaceId == source.WorkspaceId && i.OrganizationId == source.OrganizationId)
            .OrderBy(i => i.CaseId).Take(1001).ToListAsync(ct);
        if (rows.Count > 1000) throw new SurveillanceCapacityException();
        var observations = rows.Select(i => JsonSerializer.Deserialize<SurveillanceObservation>(i.ObservationJson, Json)!).ToArray();
        var affected = new[] { previous, observation }.OfType<SurveillanceObservation>().Where(o => o.OccurredAtUtc is not null).ToArray();
        var areas = affected.SelectMany(o => o.Areas).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var windows = affected.SelectMany(o => Enumerable.Range(1, SurveillanceDetector.WindowDays)
            .Select(day => new DateTimeOffset(o.OccurredAtUtc!.Value.UtcDateTime.Date, TimeSpan.Zero).AddDays(day))).ToHashSet();
        // A late report can alter an existing historical baseline as well as its own 72-hour windows.
        var existing = await db.Set<SurveillanceSignalEntity>().Where(s => s.WorkspaceId == source.WorkspaceId &&
            s.OrganizationId == source.OrganizationId && areas.Contains(s.Area)).ToListAsync(ct);
        foreach (var signal in existing)
            if (affected.Any(o => o.OccurredAtUtc >= signal.WindowEndUtc.AddDays(-31) && o.OccurredAtUtc < signal.WindowEndUtc))
                windows.Add(signal.WindowEndUtc);
        foreach (var area in areas)
        foreach (var end in windows.Order())
        foreach (var syndrome in SurveillanceDetector.Syndromes)
        {
            var assessment = SurveillanceDetector.Assess(area, syndrome, end, observations);
            var key = ClassificationFingerprint.Hash(JsonSerializer.Serialize(new { source.WorkspaceId, source.OrganizationId, area, syndrome, end, SurveillanceDetector.Version }));
            var signal = existing.SingleOrDefault(s => s.Key == key);
            if (signal is null && !assessment.Candidate) continue;
            var prior = signal is null ? null : await db.Set<DetectionAssessmentEntity>().FindAsync([signal.AssessmentId], ct);
            if (prior?.EvidenceHash == assessment.EvidenceHash) continue;
            signal ??= new() { Id = Guid.NewGuid(), Key = key, WorkspaceId = source.WorkspaceId, OrganizationId = source.OrganizationId,
                Area = area, Syndrome = syndrome, WindowEndUtc = end, ReviewState = "Candidate" };
            if (db.Entry(signal).State == EntityState.Detached) { db.Add(signal); existing.Add(signal); }
            signal.Version++;
            signal.Candidate = assessment.Candidate;
            signal.ReviewState = assessment.Candidate ? "Candidate" : "Superseded";
            var id = Guid.NewGuid();
            signal.AssessmentId = id;
            db.Add(new DetectionAssessmentEntity { Id = id, SignalId = signal.Id, Version = signal.Version, WorkId = source.Id,
                EvidenceHash = assessment.EvidenceHash, AssessmentJson = JsonSerializer.Serialize(assessment, Json), CreatedAtUtc = clock.GetUtcNow() });
            foreach (var evidence in assessment.Evidence)
                db.Add(new DetectionEvidenceEntity { AssessmentId = id, CaseId = evidence.CaseId, SourceEventId = evidence.SourceEventId, ReportRevision = evidence.ReportRevision });
            db.Add(new SurveillanceEventEntity { Id = Guid.NewGuid(), SignalId = signal.Id, Version = signal.Version,
                EventType = assessment.Candidate ? "SignalAssessed" : "SignalSuperseded", RecordedAtUtc = clock.GetUtcNow(), AvailableAtUtc = clock.GetUtcNow() });
        }
        work.Attempts++;
        work.State = "Completed";
        work.FailureCode = null;
        work.CompletedAtUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<SurveillanceObservation> ReadObservationAsync(SourceWork source, ManagedCaseEntity header,
        ClassificationProfile profile, CancellationToken ct)
    {
        var journal = await db.CaseEvents.AsNoTracking().SingleAsync(e => e.EventId == source.SourceEventId, ct);
        var bytes = await payloads.UnprotectForClassificationAsync(journal, options.Value.Organizations, ct);
        ReportContext? context;
        ReportQualityAssessment quality;
        try
        {
            var facts = CaseEventJournalReader.Read(journal, bytes) switch
            {
                CaseReported reported => reported.ReportData,
                CaseReportCorrected corrected => corrected.CorrectedReportData,
                _ => throw new InvalidDataException()
            };
            context = facts switch { CaseReportEventData.HumanV2 human => human.Report.Context, CaseReportEventData.AnimalV2 animal => animal.Report.Context, _ => null };
            quality = ReportQualityRules.Assess(facts, journal.RecordedAtUtc);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        var result = source.ResultId is { } resultId ? await db.Set<ClassificationResultEntity>().AsNoTracking().SingleAsync(r => r.Id == resultId, ct) : null;
        var completion = result?.State == "Completed" && result.AbstentionReason is null && result.PredictionsJson is { } json
            ? JsonSerializer.Deserialize<ClassificationCompletion>(json, Json) : null;
        var syndromes = completion?.Predictions.Where(p => p.Score >= profile.SelectionThreshold && SurveillanceDetector.Syndromes.Contains(p.LabelId))
            .Select(p => p.LabelId).Order(StringComparer.Ordinal).ToArray() ?? [];
        var occurrence = context?.Occurrence.Value;
        var time = occurrence is { HasValue: true } && occurrence.Value.Value.Precision == ReportedTimePrecision.Exact
            ? occurrence.Value.Value.Value?.ToUniversalTime() : null;
        var areas = new List<string>();
        if (context is not null && header.SensitivityCode == "standard" && header.Kind == CaseKind.Human)
        {
            var location = context.Location;
            areas.Add("country:" + location.CountryCode);
            var parent = location.CountryCode;
            foreach (var (level, place) in new[] { ("region", location.Region), ("district", location.Area), ("locality", location.Locality) })
            {
                if (place.Presence != PresenceState.Known) continue;
                var identity = !string.IsNullOrWhiteSpace(place.NormalizedId) ? place.NormalizedId :
                    string.IsNullOrWhiteSpace(place.ReportedName) ? null : "reported:" + ClassificationFingerprint.Normalize(place.ReportedName).ToUpperInvariant();
                if (identity is null) continue;
                parent += "/" + identity;
                areas.Add(level + ":" + parent);
            }
        }
        return new(header.CaseId, journal.EventId, journal.ReportRevision, result?.Id,
            ClassificationFingerprint.Hash(header.WorkspaceId + ":" + journal.ActorId), time, areas.ToArray(), syndromes,
            quality.Findings.Select(f => f.Code).Order(StringComparer.Ordinal).ToArray());
    }

    private sealed class SurveillanceCapacityException : Exception;
    public sealed class SourceWork
    {
        public required string Id { get; set; }
        public Guid CaseId { get; set; }
        public Guid SourceEventId { get; set; }
        public long ResourceVersion { get; set; }
        public Guid? ResultId { get; set; }
        public required string WorkspaceId { get; set; }
        public required string OrganizationId { get; set; }
    }
}
