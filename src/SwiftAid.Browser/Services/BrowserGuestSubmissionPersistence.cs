using Microsoft.JSInterop;
using SwiftAid.Client.Intake;

namespace SwiftAid.Browser.Services;

/// <summary>Encrypted browser persistence for the public guest submission queue.</summary>
public sealed class BrowserGuestSubmissionPersistence(IJSRuntime jsRuntime) : IGuestSubmissionPersistence, IAsyncDisposable
{
    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private IJSObjectReference? _module;

    public async Task<GuestSubmissionRecord?> GetAsync(Guid clientSubmissionId, CancellationToken cancellationToken)
    {
        if (clientSubmissionId == Guid.Empty) throw new ArgumentException("A report identifier is required.", nameof(clientSubmissionId));
        try
        {
            return await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
                .InvokeAsync<GuestSubmissionRecord?>("getRecord", cancellationToken, clientSubmissionId)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new GuestSubmissionStoreUnavailableException();
        }
    }

    public async Task<IReadOnlyList<GuestSubmissionRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            var records = await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
                .InvokeAsync<GuestSubmissionRecord[]>("getAllRecords", cancellationToken)
                .ConfigureAwait(false);
            return records;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new GuestSubmissionStoreUnavailableException();
        }
    }

    public async Task SaveAsync(GuestSubmissionRecord submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var current = await GetAsync(submission.ClientSubmissionId, cancellationToken).ConfigureAwait(false);
        var expectedVersion = current?.Version;
        ValidateNextVersion(submission, expectedVersion);
        await SaveVersionedAsync(submission, expectedVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveBatchAsync(
        IReadOnlyList<GuestSubmissionRecord> submissions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submissions);
        var writes = new List<GuestSubmissionWrite>(submissions.Count);
        foreach (var submission in submissions)
        {
            var current = await GetAsync(submission.ClientSubmissionId, cancellationToken).ConfigureAwait(false);
            ValidateNextVersion(submission, current?.Version);
            writes.Add(new GuestSubmissionWrite(submission, current?.Version));
        }
        await SaveBatchVersionedAsync(writes, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveVersionedAsync(
        GuestSubmissionRecord submission,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        await SaveBatchVersionedAsync([new GuestSubmissionWrite(submission, expectedVersion)], cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SaveBatchVersionedAsync(
        IReadOnlyList<GuestSubmissionWrite> writes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        try
        {
            var result = await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
                .InvokeAsync<StorageWriteResult>("saveBatchVersionedResult", cancellationToken, writes)
                .ConfigureAwait(false);
            if (result.Code == "saved") return;
            if (result.Code == "conflict")
            {
                _ = Guid.TryParse(result.ConflictId, out var conflictId);
                throw new GuestSubmissionPersistenceConflictException(conflictId);
            }
            throw new GuestSubmissionStoreUnavailableException();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GuestSubmissionPersistenceConflictException)
        {
            throw;
        }
        catch (GuestSubmissionStoreUnavailableException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new GuestSubmissionStoreUnavailableException();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            await _module.DisposeAsync().ConfigureAwait(false);
        }
        _moduleGate.Dispose();
    }

    private async ValueTask<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        if (_module is not null) return _module;
        await _moduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _module ??= await jsRuntime.InvokeAsync<IJSObjectReference>(
                "import",
                cancellationToken,
                "./js/guest-storage.js").ConfigureAwait(false);
            return _module;
        }
        finally
        {
            _moduleGate.Release();
        }
    }

    private static void ValidateNextVersion(GuestSubmissionRecord submission, long? expectedVersion)
    {
        var required = expectedVersion is null ? 1 : checked(expectedVersion.Value + 1);
        if (expectedVersion is < 1 || submission.Version != required)
        {
            throw new GuestSubmissionPersistenceConflictException(submission.ClientSubmissionId);
        }
    }

    private sealed record StorageWriteResult(string Code, string? ConflictId = null);
}
