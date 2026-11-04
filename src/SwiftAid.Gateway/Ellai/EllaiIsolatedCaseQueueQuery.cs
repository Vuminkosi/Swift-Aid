using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Runs the existing authorized queue read in a scope that survives caller cancellation.</summary>
internal sealed class EllaiIsolatedCaseQueueQuery(IServiceProvider services, EllaiIsolatedToolCalls calls) : ICaseQueueQuery
{
    public Task<CaseQueueResult> GetSearchableAsync(ActorContext actor, int? limit, CaseQueueFilter? filter,
        string? cursor, CancellationToken ct) => calls.RunAsync(async token =>
        {
            await using var scope = services.CreateAsyncScope();
            var query = scope.ServiceProvider.GetRequiredService<ICaseQueueQuery>();
            token.ThrowIfCancellationRequested();
            return await query.GetSearchableAsync(actor, limit, filter, cursor, token).ConfigureAwait(false);
        }, ct);

    public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) =>
        ReadAsync(actor, limit, null, null, ct);

    public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, string? cursor, CancellationToken ct) =>
        ReadAsync(actor, limit, null, cursor, ct);

    public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, CancellationToken ct) =>
        ReadAsync(actor, limit, filter, null, ct);

    public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, string? cursor, CancellationToken ct) =>
        ReadAsync(actor, limit, filter, cursor, ct);

    private Task<CaseQueueResult> ReadAsync(ActorContext actor, int? limit, CaseQueueFilter? filter, string? cursor, CancellationToken ct) =>
        calls.RunAsync(async token =>
        {
            await using var scope = services.CreateAsyncScope();
            var query = scope.ServiceProvider.GetRequiredService<ICaseQueueQuery>();
            token.ThrowIfCancellationRequested();
            return filter is null
                ? await query.GetMineAsync(actor, limit, cursor, token).ConfigureAwait(false)
                : await query.GetMineAsync(actor, limit, filter, cursor, token).ConfigureAwait(false);
        }, ct);
}
