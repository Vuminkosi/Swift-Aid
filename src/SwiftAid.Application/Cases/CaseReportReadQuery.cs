using System.Collections.Immutable;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Cases;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Cases;

public interface ICaseReportReadQuery
{
    Task<CaseReportDetails?> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct);
}

/// <summary>Occurrence location from a currently authorized report, without report evidence.</summary>
public interface ICaseOccurrenceLocationQuery
{
    Task<CaseOccurrenceLocationResult> GetLocationAsync(ActorContext actor, Guid caseId, CancellationToken ct);
}

public enum CaseOccurrenceLocationAccess { Allowed, NotFoundOrNotAuthorized, Unavailable }

public sealed record CaseOccurrenceLocationResult(CaseOccurrenceLocationAccess Access, long? ReportRevision, CaseReportPlace? Location);

/// <summary>Reuses the protected journal under a read lease; no derived index or unguarded payload read.</summary>
public sealed class CaseReportReadQuery(
    ICaseAccessPolicy policy,
    ICaseCommandUnitOfWork unitOfWork,
    ICaseCommandPersistence persistence,
    IGeographicRegistry? registry = null) : ICaseReportReadQuery, ICaseOccurrenceLocationQuery
{
    public async Task<CaseOccurrenceLocationResult> GetLocationAsync(ActorContext actor, Guid caseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ct.ThrowIfCancellationRequested();
        if (caseId == Guid.Empty) return new(CaseOccurrenceLocationAccess.NotFoundOrNotAuthorized, null, null);
        try
        {
            ICaseCommandLease lease;
            try { lease = await unitOfWork.BeginCaseAsync(actor, caseId, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct); }
            catch (KeyNotFoundException) { return new(CaseOccurrenceLocationAccess.NotFoundOrNotAuthorized, null, null); }
            await using var ownedLease = lease;
            var decision = await policy.AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, ct);
            ct.ThrowIfCancellationRequested();
            if (!decision.IsAllowed) return new(decision.Code is AccessDecisionCode.GrantSourceUnavailable or AccessDecisionCode.ResourceSourceUnavailable
                ? CaseOccurrenceLocationAccess.Unavailable : CaseOccurrenceLocationAccess.NotFoundOrNotAuthorized, null, null);
            var aggregate = await persistence.ReplayCaseAsync(lease, ct);
            ct.ThrowIfCancellationRequested();
            if (aggregate.CaseId != caseId || aggregate.ReportData is null)
                return new(CaseOccurrenceLocationAccess.Unavailable, null, null);
            var place = ProjectLocation(aggregate);
            await lease.CommitAsync(ct);
            ct.ThrowIfCancellationRequested();
            return new(CaseOccurrenceLocationAccess.Allowed, aggregate.ReportRevision, place);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { return new(CaseOccurrenceLocationAccess.Unavailable, null, null); }
    }

    public async Task<CaseReportDetails?> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct)
    {
        if (caseId == Guid.Empty) return null;
        ICaseCommandLease lease;
        try
        {
            lease = await unitOfWork.BeginCaseAsync(actor, caseId, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct);
        }
        catch (KeyNotFoundException) { return null; }
        await using (lease)
        {
            var decision = await policy.AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, ct);
            if (!decision.IsAllowed) return null;
            var aggregate = await persistence.ReplayCaseAsync(lease, ct);
            if (aggregate.CaseId != caseId || aggregate.ReportData is null) return null;
            var result = Project(aggregate);
            if (result.NearbyFacilities is null && registry is not null)
                result = result with { NearbyFacilities = await NearbyMedicalFacilities.FindFromLocalityAsync(result.Location, registry, ct) };
            await lease.CommitAsync(ct);
            return result;
        }
    }

    public static CaseReportDetails Project(CaseAggregate aggregate)
    {
        var evidence = aggregate.ReportData switch
        {
            CaseReportEventData.HumanV2 human => human.Report.Evidence,
            CaseReportEventData.AnimalV2 animal => animal.Report.Evidence,
            _ => null
        };
        var location = aggregate.ReportData switch
        {
            CaseReportEventData.HumanV2 human => human.Report.Context.Location,
            CaseReportEventData.AnimalV2 animal => animal.Report.Context.Location,
            _ => null
        };
        var legacy = aggregate.ReportData as CaseReportEventData.LegacyV1;
        var observations = evidence?.Observations.Select(item => item.Text)
            ?? legacy?.Report.Observations.Select(item => item.Narrative)
            ?? Enumerable.Empty<string>();
        // Follow-up observations remain verbatim and follow the current corrected report evidence.
        observations = observations.Concat(aggregate.Events.OfType<CaseObservationRecorded>().Select(item => item.Narrative));
        return new(aggregate.CaseId, aggregate.ReportRevision,
            evidence?.Narrative?.Text ?? legacy?.Report.Narrative, observations.ToArray(),
            ProjectLocation(aggregate), NearbyMedicalFacilities.Find(location));
    }

    private static CaseReportPlace? ProjectLocation(CaseAggregate aggregate)
    {
        var location = aggregate.ReportData switch
        {
            CaseReportEventData.HumanV2 human => human.Report.Context.Location,
            CaseReportEventData.AnimalV2 animal => animal.Report.Context.Location,
            _ => null
        };
        return location is null ? null : new(location.CountryCode, location.Region.ReportedName,
            location.Area.ReportedName, location.Locality.ReportedName, location.Region.NormalizedId,
            location.Area.NormalizedId, location.Locality.NormalizedId);
    }
}
