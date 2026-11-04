using Microsoft.AspNetCore.Components.Authorization;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake.Imports;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Gateway.Ingestion;

public sealed record ImportDestination(string WorkspaceId, string OrganizationId);

public interface IWorkspaceImportClient
{
    Task<IReadOnlyList<ImportDestination>> GetDestinationsAsync(CancellationToken ct);
    Task<IReadOnlyList<CaseImportWorkspace>> GetWorkspacesAsync(CancellationToken ct);
    Task<IReadOnlyList<CaseImportHistoryItem>> GetRecentAsync(CaseImportWorkspace workspace, CancellationToken ct);
    Task<CaseImportSummary?> GetAsync(Guid importId, CancellationToken ct);
    Task<bool> MatchesContextAsync(Guid importId, CaseImportContext context, CancellationToken ct);
    Task<IReadOnlyList<CaseImportRowOutcome>> GetIssuesAsync(Guid importId, long afterRow, CancellationToken ct);
    Task<CaseImportBatchResult> ImportAsync(Guid importId, CaseImportContext context, CaseCsvBatch batch, CancellationToken ct);
}

/// <summary>Each interactive operation owns a short database scope; the browser cannot supply actor identity.</summary>
public sealed class WorkspaceImportClient(AuthenticationStateProvider authentication, IServiceScopeFactory scopes) : IWorkspaceImportClient
{
    public Task<IReadOnlyList<ImportDestination>> GetDestinationsAsync(CancellationToken ct) => InScopeAsync(async (services, actor) =>
    {
        var db = services.GetRequiredService<SwiftAidDbContext>();
        var now = DateTimeOffset.UtcNow;
        return (IReadOnlyList<ImportDestination>)await (
            from membership in db.OrganizationMemberships.AsNoTracking()
            join person in db.Actors on membership.ActorId equals person.ActorId
            join state in db.ActorAccessStates on person.ActorId equals state.ActorId
            join organization in db.Organizations on membership.OrganizationId equals organization.OrganizationId
            join link in db.WorkspaceOrganizations on organization.OrganizationId equals link.OrganizationId
            join workspace in db.Workspaces on link.WorkspaceId equals workspace.WorkspaceId
            where person.ActorId == actor.ActorId && state.AccessEpoch == actor.AccessEpoch &&
                person.Status == PersistentActorStatus.Active && (person.ExpiresAtUtc == null || person.ExpiresAtUtc > now) &&
                membership.Status == PersistentMembershipStatus.Active && membership.ValidFromUtc <= now &&
                (membership.ExpiresAtUtc == null || membership.ExpiresAtUtc > now) &&
                organization.Status == PersistentResourceStatus.Active && link.Status == PersistentResourceStatus.Active &&
                workspace.Status == PersistentResourceStatus.Active
            orderby organization.OrganizationId, workspace.WorkspaceId
            select new ImportDestination(workspace.WorkspaceId, organization.OrganizationId)).Distinct().ToArrayAsync(ct);
    });

    public Task<IReadOnlyList<CaseImportWorkspace>> GetWorkspacesAsync(CancellationToken ct) =>
        InScopeAsync((services, actor) => services.GetRequiredService<ICaseImportWorkspaceReader>().GetWorkspacesAsync(actor, ct));

    public Task<IReadOnlyList<CaseImportHistoryItem>> GetRecentAsync(CaseImportWorkspace workspace, CancellationToken ct) =>
        InScopeAsync((services, actor) => services.GetRequiredService<ICaseImportWorkspaceReader>()
            .GetRecentAsync(actor, workspace.WorkspaceId, workspace.CountryCode, ct, workspace.OrganizationId));

    public Task<CaseImportSummary?> GetAsync(Guid importId, CancellationToken ct) =>
        InScopeAsync((services, actor) => services.GetRequiredService<ICaseImportStore>().GetAsync(actor, importId, ct));

    public Task<bool> MatchesContextAsync(Guid importId, CaseImportContext context, CancellationToken ct) =>
        InScopeAsync(async (services, actor) =>
            await services.GetRequiredService<ICaseImportStore>().MatchesContextAsync(actor, importId, context, ct)
                ?? throw new CaseImportException("not-authorized"));

    public Task<IReadOnlyList<CaseImportRowOutcome>> GetIssuesAsync(Guid importId, long afterRow, CancellationToken ct) =>
        InScopeAsync((services, actor) => services.GetRequiredService<ICaseImportWorkspaceReader>().GetIssuesAsync(actor, importId, afterRow, ct));

    public Task<CaseImportBatchResult> ImportAsync(Guid importId, CaseImportContext context, CaseCsvBatch batch, CancellationToken ct) =>
        InScopeAsync(async (services, actor) =>
        {
            using var reader = new StringReader(batch.Csv);
            return await services.GetRequiredService<CaseCsvImportService>()
                .ProcessAsync(actor, importId, context, batch.StartRow, batch.IsFinal, reader, ct);
        });

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, ActorContext, Task<T>> operation)
    {
        var actor = ActorContext.FromValidatedPrincipal((await authentication.GetAuthenticationStateAsync()).User);
        await using var scope = scopes.CreateAsyncScope();
        return await operation(scope.ServiceProvider, actor);
    }
}
