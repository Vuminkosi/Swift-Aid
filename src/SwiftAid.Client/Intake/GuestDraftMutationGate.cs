namespace SwiftAid.Client.Intake;

/// <summary>An acquired guest-draft mutation lease. Dispose it to release the shared mutation gate.</summary>
public interface IGuestDraftMutationLease : IAsyncDisposable
{
}

/// <summary>Serializes guest-draft and audio metadata mutations across Client and Mobile services.</summary>
public interface IGuestDraftMutationGate
{
    ValueTask<IGuestDraftMutationLease> AcquireAsync(CancellationToken cancellationToken = default);
}

/// <summary>Process-wide default gate; hosts should register and inject this same instance into all writers.</summary>
public sealed class GuestDraftMutationGate : IGuestDraftMutationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public static GuestDraftMutationGate Instance { get; } = new();

    public async ValueTask<IGuestDraftMutationLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_semaphore);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IGuestDraftMutationLease
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
