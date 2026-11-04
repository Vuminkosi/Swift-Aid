using Microsoft.JSInterop;
using SwiftAid.Client.Intake;

namespace SwiftAid.Browser.Services;

/// <summary>Encrypted, restart-safe journal for superseded browser guest recordings.</summary>
public sealed class BrowserGuestAudioCleanupJournal(IJSRuntime jsRuntime) : IGuestAudioCleanupJournal, IAsyncDisposable
{
    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private IJSObjectReference? _module;

    public async Task<GuestAudioCleanupJournalResult> EnsurePendingAsync(
        GuestAudioCleanupIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!intent.IsValid) return new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.InvalidRequest);
        try
        {
            var response = await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
                .InvokeAsync<CodeResponse>("ensureCleanupPendingResult", cancellationToken, intent)
                .ConfigureAwait(false);
            return response.Code switch
            {
                "pending" => new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Recorded),
                "conflict" => new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Failed),
                "invalid-request" => new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.InvalidRequest),
                _ => new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Unavailable)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Unavailable);
        }
    }

    public async Task<IReadOnlyList<GuestAudioCleanupIntent>> GetPendingAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
                .InvokeAsync<PendingResponse>("getPendingCleanupResult", cancellationToken)
                .ConfigureAwait(false);
            if (response.Code != "ok") throw new GuestSubmissionStoreUnavailableException();
            return response.Intents;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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

    public async Task<GuestAudioCleanupJournalResult> CompleteAsync(
        string intentKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(intentKey))
            return new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.InvalidRequest);
        try
        {
            var response = await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
                .InvokeAsync<CodeResponse>("getCleanupStatusResult", cancellationToken, intentKey)
                .ConfigureAwait(false);
            return response.Code switch
            {
                "completed" => new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.AlreadyCompleted),
                "pending" => new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Failed),
                "not-found" => new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.InvalidRequest),
                _ => new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Unavailable)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Unavailable);
        }
    }

    public async Task<GuestAudioCleanupJournalResult> CancelAsync(
        string intentKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(intentKey))
            return new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.InvalidRequest);
        try
        {
            var response = await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
                .InvokeAsync<CodeResponse>("cancelCleanupResult", cancellationToken, intentKey)
                .ConfigureAwait(false);
            return response.Code switch
            {
                "cancelled" => new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Cancelled),
                "already-cancelled" => new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.AlreadyCancelled),
                "not-found" => new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.InvalidRequest),
                "completed" => new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.AlreadyCompleted),
                _ => new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Unavailable)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GuestAudioCleanupJournalResult(GuestAudioCleanupJournalCode.Unavailable);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await _module.DisposeAsync().ConfigureAwait(false);
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

    private sealed record CodeResponse(string Code);
    private sealed record PendingResponse(string Code, GuestAudioCleanupIntent[] Intents);
}
