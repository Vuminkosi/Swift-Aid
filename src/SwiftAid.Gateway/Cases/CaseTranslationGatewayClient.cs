using Microsoft.AspNetCore.Components.Authorization;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Cases;
using SwiftAid.Infrastructure.Classification;

namespace SwiftAid.Gateway.Cases;

public sealed class CaseTranslationGatewayClient(AuthenticationStateProvider authentication, IServiceScopeFactory scopes) : ICaseTranslationClient
{
    public async Task<CaseTranslationSnapshot> GetAsync(Guid caseId, CancellationToken cancellationToken = default)
    {
        try
        {
            var actor = ActorContext.FromValidatedPrincipal((await authentication.GetAuthenticationStateAsync()).User);
            await using var scope = scopes.CreateAsyncScope();
            var items = await scope.ServiceProvider.GetRequiredService<CaseTranslationService>().ReadAsync(actor, caseId, cancellationToken);
            return items is null ? new(caseId, [], CaseClassificationAccess.Denied) : new(caseId, items.Select(x =>
                new CaseTranslationItem(x.ArtifactId, x.State, x.SourceLanguage, x.TargetLanguage, x.ModelId, x.ModelRevision,
                    x.ReviewStatus, x.ReviewVersion, x.Findings, x.TranslatedText, x.FailureCode, x.CreatedAtUtc, x.OriginalText, x.CanReview)).ToArray());
        }
        catch (UnauthorizedAccessException) { return new(caseId, [], CaseClassificationAccess.Denied); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(caseId, [], CaseClassificationAccess.Unavailable); }
    }

    public async Task<string> ReviewAsync(Guid caseId, Guid artifactId, Guid decisionId, long expectedVersion, bool approve, CancellationToken cancellationToken = default)
    {
        try
        {
            var actor = ActorContext.FromValidatedPrincipal((await authentication.GetAuthenticationStateAsync()).User);
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<CaseTranslationService>().ReviewAsync(actor, caseId, artifactId,
                new(decisionId, expectedVersion, approve), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return "Unavailable"; }
    }
}
