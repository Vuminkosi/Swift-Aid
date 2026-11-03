using SwiftAid.Application.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Runs the existing derived-index read in a scope that survives cancellation.</summary>
internal sealed class EllaiIsolatedExactReferenceIndex(IServiceProvider services, EllaiIsolatedToolCalls calls)
    : IEllaiExactReferenceIndex
{
    public Task<EllaiExactMemoryReference?> FindExactAsync(string id, string sessionId, string scopeEpoch, CancellationToken ct) =>
        calls.RunAsync(async token =>
        {
            await using var scope = services.CreateAsyncScope();
            var index = scope.ServiceProvider.GetRequiredService<IEllaiExactReferenceIndex>();
            token.ThrowIfCancellationRequested();
            return await index.FindExactAsync(id, sessionId, scopeEpoch, token).ConfigureAwait(false);
        }, ct);
}
