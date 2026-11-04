using Microsoft.AspNetCore.Components.Authorization;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Classification;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Cases;

public sealed class CaseClassificationGatewayClient(AuthenticationStateProvider authentication, IServiceScopeFactory scopes) : ICaseClassificationClient
{
    public async Task<CaseClassificationSnapshot> GetAsync(Guid caseId, CancellationToken cancellationToken = default)
    {
        if (caseId == Guid.Empty) return new(caseId, [], CaseClassificationAccess.Unavailable);
        try
        {
            var state = await authentication.GetAuthenticationStateAsync();
            if (state.User.Identity?.IsAuthenticated != true) return new(caseId, [], CaseClassificationAccess.Denied);
            var actor = ActorContext.FromValidatedPrincipal(state.User);
            // Overview panels load concurrently; each authorized read owns its DbContext and transaction.
            await using var scope = scopes.CreateAsyncScope();
            var query = scope.ServiceProvider.GetRequiredService<ICaseClassificationQuery>();
            var items = await query.GetAsync(actor, caseId, cancellationToken);
            return items is null ? new(caseId, [], CaseClassificationAccess.Denied) : new(caseId,
                items.Select(i => new CaseClassificationItem(i.ResultId, i.ReportRevision, i.State, i.CurrentConfiguration,
                    i.SourceLanguage, i.AbstentionReason, i.CreatedAtUtc,
                    i.Labels.Select(l => new CaseClassificationPattern(l.Id, l.Description, l.SemanticScore, l.Selected)).ToArray(),
                    i.Quality is null ? null : new CaseQualityEvidence(i.Quality.Version,
                        i.Quality.Findings.Select(f => new CaseQualityFinding(f.Code, f.Field, f.Explanation)).ToArray()),
                    i.Translation is null ? null : new CaseTranslationProvenance(i.Translation.ArtifactId, i.Translation.Fingerprint,
                        i.Translation.SourceLanguage, i.Translation.ModelRevision, i.Translation.ReviewStatus, i.Translation.ReviewVersion))).ToArray());
        }
        catch (UnauthorizedAccessException) { return new(caseId, [], CaseClassificationAccess.Denied); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(caseId, [], CaseClassificationAccess.Unavailable); }
    }
}
