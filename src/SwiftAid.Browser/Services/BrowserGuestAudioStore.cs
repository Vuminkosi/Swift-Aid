using Microsoft.JSInterop;
using SwiftAid.Client.Intake;

namespace SwiftAid.Browser.Services;

/// <summary>Stores browser guest audio encrypted and bound to its persisted report revision.</summary>
public sealed class BrowserGuestAudioStore(
    IJSRuntime jsRuntime,
    BrowserGuestSubmissionPersistence submissions) : IGuestAudioStore, IAsyncDisposable
{
    private const int MaximumAudioBytes = 10 * 1024 * 1024;
    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private IJSObjectReference? _module;

    public async Task<GuestAudioAsset> SaveOriginalAsync(
        Guid clientSubmissionId,
        Guid attachmentId,
        string mediaType,
        int durationMilliseconds,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (clientSubmissionId == Guid.Empty || attachmentId == Guid.Empty)
        {
            throw new ArgumentException("A report and recording identifier are required.");
        }

        var record = await submissions.GetAsync(clientSubmissionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The saved report could not be found.");
        if (record.State is not (GuestSubmissionLocalState.Draft or GuestSubmissionLocalState.NeedsAttention) ||
            !record.Submission.OriginalAudioAttachmentIds.Contains(attachmentId))
        {
            throw new InvalidOperationException("This recording is not attached to an editable saved report.");
        }

        var bytes = await ReadBoundedAsync(content, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
                .InvokeAsync<AudioSaveResponse>(
                    "saveAudioForDraftResult",
                    cancellationToken,
                    record,
                    record.Version,
                    attachmentId,
                    mediaType,
                    durationMilliseconds,
                    bytes)
                .ConfigureAwait(false);

            return result.Code switch
            {
                "saved" when result.Asset is not null => result.Asset,
                "conflict" => throw new GuestSubmissionPersistenceConflictException(clientSubmissionId),
                "invalid-request" => throw new InvalidOperationException("The recording could not be saved to this report."),
                _ => throw new GuestSubmissionStoreUnavailableException()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GuestSubmissionPersistenceConflictException)
        {
            throw;
        }
        catch (InvalidOperationException)
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
        finally
        {
            Array.Clear(bytes);
        }
    }

    public async Task<byte[]> ReadOriginalAsync(
        Guid clientSubmissionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (clientSubmissionId == Guid.Empty || attachmentId == Guid.Empty)
        {
            throw new ArgumentException("A report and recording identifier are required.");
        }

        try
        {
            var result = await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
                .InvokeAsync<AudioReadResponse?>(
                    "readAudio",
                    cancellationToken,
                    clientSubmissionId,
                    attachmentId)
                .ConfigureAwait(false);
            return result?.Bytes ?? throw new KeyNotFoundException("The saved recording could not be found.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (KeyNotFoundException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new GuestSubmissionStoreUnavailableException();
        }
    }

    public async Task<GuestAudioCleanupResult> CleanupSupersededOriginalAsync(
        GuestAudioCleanupIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!intent.IsValid)
        {
            return new GuestAudioCleanupResult(GuestAudioCleanupCode.InvalidRequest);
        }

        try
        {
            var response = await (await GetModuleAsync(cancellationToken).ConfigureAwait(false))
                .InvokeAsync<CodeResponse>("completeAudioCleanupResult", cancellationToken, intent.IntentKey)
                .ConfigureAwait(false);
            return response.Code switch
            {
                "removed" => new GuestAudioCleanupResult(GuestAudioCleanupCode.Removed),
                "retained" => new GuestAudioCleanupResult(GuestAudioCleanupCode.RetainedForHistory),
                "not-safe" => new GuestAudioCleanupResult(GuestAudioCleanupCode.NotSafe),
                _ => new GuestAudioCleanupResult(GuestAudioCleanupCode.Unavailable)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GuestAudioCleanupResult(GuestAudioCleanupCode.Unavailable);
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

    private static async Task<byte[]> ReadBoundedAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        try
        {
            while (true)
            {
                var read = await content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaximumAudioBytes)
                {
                    throw new InvalidOperationException("The recording is larger than the supported limit.");
                }
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            return buffer.ToArray();
        }
        finally
        {
            Array.Clear(chunk);
            if (buffer.TryGetBuffer(out var segment)) Array.Clear(segment.Array!, segment.Offset, segment.Count);
        }
    }

    private sealed record AudioSaveResponse(string Code, GuestAudioAsset? Asset = null);
    private sealed record AudioReadResponse(byte[] Bytes, GuestAudioAsset Asset);
    private sealed record CodeResponse(string Code);
}
