namespace SwiftAid.Gateway.Ellai;

/// <summary>Bounds the number of isolated tool calls that can outlive a cancelled turn.</summary>
internal sealed class EllaiIsolatedToolCalls(int maximumConcurrent = 8, TimeSpan? caseReadLimit = null)
{
    private readonly SemaphoreSlim _slots = maximumConcurrent > 0
        ? new SemaphoreSlim(maximumConcurrent, maximumConcurrent)
        : throw new ArgumentOutOfRangeException(nameof(maximumConcurrent));
    private readonly TimeSpan _caseReadLimit = caseReadLimit is null ? TimeSpan.FromSeconds(35)
        : caseReadLimit > TimeSpan.Zero && caseReadLimit <= TimeSpan.FromMinutes(5)
            ? caseReadLimit.Value : throw new ArgumentOutOfRangeException(nameof(caseReadLimit));

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        var task = Task.Run(async () =>
        {
            try
            {
                // Isolated reads can outlive their caller. Bound any PostgreSQL
                // case-authority read lease they create, including nested scopes.
                using var leaseDeadline = SwiftAid.Application.Cases.Commands.CaseAuthorityReadDeadline.Begin(_caseReadLimit);
                ct.ThrowIfCancellationRequested();
                return await operation(ct).ConfigureAwait(false);
            }
            finally { _slots.Release(); }
        }, CancellationToken.None);
        try
        {
            var result = await task.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception error) when (ct.IsCancellationRequested && error is not OutOfMemoryException)
        {
            // The independent scope remains owned by the operation until it exits.
            // Observe any later fault without sending its result back to the agent.
            _ = task.ContinueWith(static completed => _ = completed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new OperationCanceledException(ct);
        }
    }
}
