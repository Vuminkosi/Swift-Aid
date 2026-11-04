using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Intake;
using SwiftAid.Application.Surveillance;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Surveillance;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Surveillance;

namespace SwiftAid.Gateway.Cases;

public sealed class SurveillanceMapDetailsGatewayClient(ICaseReportDetailsClient reports,
    IWorkspaceAccessClient workspaces, IServiceScopeFactory scopes, AuthenticationStateProvider authentication,
    ILogger<SurveillanceMapDetailsGatewayClient> logger) : ISurveillanceMapDetailsClient
{
    public async Task<UiCapabilityResult<SurveillanceMapDetails>> GetAsync(string countryCode, MapAggregatePeriod period, CancellationToken ct = default)
    {
        if (countryCode.Length != 2 || !countryCode.All(char.IsAsciiLetter) || !Enum.IsDefined(period))
            return UiCapabilityResults.Unavailable<SurveillanceMapDetails>("surveillance-map-details");
        try
        {
            var workspace = await workspaces.GetStateAsync(ct);
            if (!workspace.Succeeded || workspace.Value?.SelectedWorkspaceId is not { } workspaceId)
                return UiCapabilityResults.Unavailable<SurveillanceMapDetails>("surveillance-map-details");
            var actor = ActorContext.FromValidatedPrincipal((await authentication.GetAuthenticationStateAsync()).User);
            var since = DateTimeOffset.UtcNow.AddDays(-(int)period);
            await using var scope = scopes.CreateAsyncScope();
            var candidates = await scope.ServiceProvider.GetRequiredService<ICaseMapCandidateStore>()
                .GetAsync(actor.ActorId, workspaceId, countryCode, since, 501, ct);
            var rows = candidates.Where(item => item.WorkspaceId == workspaceId && item.CountryCode == countryCode && item.CreatedAtUtc >= since).Take(500).ToArray();
            var places = new List<(CaseReportPlace Place, bool Animal, SurveillanceMapCase Case)>();
            var unmapped = 0;
            foreach (var batch in rows.Chunk(8))
            {
                var results = await Task.WhenAll(batch.Select(async row => (Row: row, Report: await reports.GetAsync(row.CaseId, ct))));
                foreach (var result in results)
                {
                    if (result.Report.Succeeded && result.Report.Value?.Location is { } place)
                        places.Add((place, result.Row.Sector == "AnimalHealth", new(result.Row.CaseId,
                            result.Row.Sector == "AnimalHealth", result.Row.Workflow, result.Row.CreatedAtUtc!.Value)));
                    else unmapped++;
                }
            }
            var registry = scope.ServiceProvider.GetRequiredService<IGeographicRegistry>();
            var points = new List<SurveillanceMapPoint>();
            foreach (var group in places.GroupBy(item => (item.Place.LocalityId ?? item.Place.AreaId ?? item.Place.RegionId,
                         item.Place.Region, Name: item.Place.Locality ?? item.Place.Area ?? item.Place.Region)))
            {
                ct.ThrowIfCancellationRequested();
                var humanCount = group.Count(item => !item.Animal);
                var animalCount = group.Count(item => item.Animal);
                // Layer toggles must not expose a small subgroup through subtraction.
                if (humanCount is > 0 and < 5 || animalCount is > 0 and < 5 || string.IsNullOrWhiteSpace(group.Key.Name))
                {
                    unmapped += group.Count();
                    continue;
                }
                var found = await registry.SearchAsync(group.Key.Name, countryCode, 20, ct);
                var matches = found.Results.Where(item =>
                    (group.Key.Item1 is { } id ? item.Id == id : string.Equals(item.Name, group.Key.Name, StringComparison.OrdinalIgnoreCase)) &&
                    (group.Key.Region is null || string.Equals(item.Name, group.Key.Region, StringComparison.OrdinalIgnoreCase) ||
                     item.Ancestors.Any(parent => string.Equals(parent.Name, group.Key.Region, StringComparison.OrdinalIgnoreCase)))).ToArray();
                if (matches is not [{ Latitude: { } latitude, Longitude: { } longitude } match]) { unmapped += group.Count(); continue; }
                points.Add(new(match.Id, match.Name, latitude, longitude, humanCount, animalCount,
                    group.Select(item => item.Case).OrderByDescending(item => item.ReportedAtUtc).ToArray()));
            }
            var signals = new List<SurveillanceMapSignal>();
            var review = scope.ServiceProvider.GetService<ISignalReviewService>();
            if (review?.Enabled == true)
            {
                var db = scope.ServiceProvider.GetRequiredService<SwiftAidDbContext>();
                var ids = await db.Set<SurveillanceSignalEntity>().AsNoTracking()
                    .Where(item => item.WorkspaceId == workspaceId && item.Candidate && item.WindowEndUtc >= since)
                    .OrderByDescending(item => item.WindowEndUtc).Select(item => item.Id).Take(10).ToArrayAsync(ct);
                foreach (var id in ids)
                {
                    var dossier = await review.GetAsync(actor, id, ct);
                    if (dossier is null || !dossier.Assessment.Evidence.Any(evidence => rows.Any(row => row.CaseId == evidence.CaseId))) continue;
                    var assessment = dossier.Assessment;
                    signals.Add(new(id, assessment.Syndrome + " reports", assessment.Area, dossier.State,
                        assessment.Observed, assessment.WindowStartUtc, assessment.WindowEndUtc, assessment.Comparison,
                        "/case/" + assessment.Evidence[0].CaseId.ToString("D")));
                }
            }
            return new(UiCapabilityResultCode.Success, new(points, signals, candidates.Count > 500, unmapped),
                new("surveillance-map-details", UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning("Surveillance map details unavailable: {FailureType}", exception.GetType().Name);
            return UiCapabilityResults.Unavailable<SurveillanceMapDetails>("surveillance-map-details");
        }
    }
}
