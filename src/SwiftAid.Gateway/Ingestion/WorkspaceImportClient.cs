using Microsoft.AspNetCore.Components.Authorization;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake.Imports;

namespace SwiftAid.Gateway.Ingestion;

public interface IWorkspaceImportClient
{
    Task<IReadOnlyList<CaseImportWorkspace>> GetWorkspacesAsync(CancellationToken ct);
    Task<IReadOnlyList<CaseImportHistoryItem>> GetRecentAsync(CaseImportWorkspace workspace, CancellationToken ct);
    Task<CaseImportSummary?> GetAsync(Guid importId, CancellationToken ct);
    Task<IReadOnlyList<CaseImportRowOutcome>> GetIssuesAsync(Guid importId, long afterRow, CancellationToken ct);
    Task<CaseImportBatchResult> ImportAsync(Guid importId, CaseImportContext context, CaseCsvBatch batch, CancellationToken ct);
}

/// <summary>Each interactive operation owns a short database scope; the browser cannot supply actor identity.</summary>
public sealed class WorkspaceImportClient(AuthenticationStateProvider authentication, IServiceScopeFactory scopes) : IWorkspaceImportClient
{
    public Task<IReadOnlyList<CaseImportWorkspace>> GetWorkspacesAsync(CancellationToken ct) =>
        InScopeAsync((services, actor) => services.GetRequiredService<ICaseImportWorkspaceReader>().GetWorkspacesAsync(actor, ct));

    public Task<IReadOnlyList<CaseImportHistoryItem>> GetRecentAsync(CaseImportWorkspace workspace, CancellationToken ct) =>
        InScopeAsync((services, actor) => services.GetRequiredService<ICaseImportWorkspaceReader>()
            .GetRecentAsync(actor, workspace.WorkspaceId, workspace.CountryCode, ct));

    public Task<CaseImportSummary?> GetAsync(Guid importId, CancellationToken ct) =>
        InScopeAsync((services, actor) => services.GetRequiredService<ICaseImportStore>().GetAsync(actor, importId, ct));

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
