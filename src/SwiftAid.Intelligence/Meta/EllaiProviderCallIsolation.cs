using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Bounds provider operations that can outlive a cancelled caller.</summary>
internal sealed class EllaiProviderCallIsolation(int maximumConcurrent = 8)
{
    public static EllaiProviderCallIsolation Instance { get; } = new();
    private readonly SemaphoreSlim _slots = maximumConcurrent > 0
        ? new SemaphoreSlim(maximumConcurrent, maximumConcurrent)
        : throw new ArgumentOutOfRangeException(nameof(maximumConcurrent));

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> invoke, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        var operation = Task.Run(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                return await invoke(ct).ConfigureAwait(false);
            }
            finally { _slots.Release(); }
        }, CancellationToken.None);
        try
        {
            return await operation.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Allow a response already completing to report usage, without waiting
            // indefinitely for a provider that ignores cancellation.
            if (!operation.IsCompleted)
                await Task.WhenAny(operation, Task.Delay(TimeSpan.FromMilliseconds(100))).ConfigureAwait(false);
            if (operation.IsCompletedSuccessfully) return operation.Result;
            _ = operation.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    public async IAsyncEnumerable<T> StreamAsync<T>(Func<CancellationToken, IAsyncEnumerator<T>> create,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateBounded<(T Item, TaskCompletionSource Acknowledged)>(1);
        var producer = Task.Run(async () =>
        {
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                await using (var enumerator = create(stop.Token))
                {
                    while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        await channel.Writer.WriteAsync((enumerator.Current, acknowledged), stop.Token).ConfigureAwait(false);
                        await acknowledged.Task.WaitAsync(stop.Token).ConfigureAwait(false);
                    }
                }
                channel.Writer.TryComplete();
            }
            catch (Exception error) { channel.Writer.TryComplete(error); }
            finally { _slots.Release(); }
        }, CancellationToken.None);
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try { yield return item.Item; }
                finally { item.Acknowledged.TrySetResult(); }
            }
        }
        finally
        {
            try { stop.Cancel(); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            finally
            {
                _ = producer.ContinueWith(_ => stop.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
}
