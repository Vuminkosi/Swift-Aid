using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Intelligence.Meta;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Session-scoped voice consent and bounded, transient ASR dispatch.
/// Captured conversational audio is never written to case evidence storage.</summary>
public sealed class EllaiVoiceTranscriptionService(
    EllaiConsentService consent,
    IEllaiConsentStore consentStore,
    EllaiOptions options,
    IServiceProvider services,
    ILogger<EllaiVoiceTranscriptionService>? logger = null)
{
    public const string AudioDataClass = "voice-audio-transient";
    public const int MaximumAudioBytes = 32 * 1024 * 1024;

    public async Task<bool> GrantVoiceConsentAsync(string sessionId, CancellationToken cancellationToken)
    {
        var grants = new List<EllaiConsentGrant>(2);
        foreach (var dimension in new[] { EllaiConsentDimension.Microphone, EllaiConsentDimension.Transcription })
        {
            grants.Add(consent.Grant(sessionId, dimension, EllaiConsentPurposes.VoiceTranscription,
                AudioDataClass, TimeSpan.FromHours(1)));
        }

        try
        {
            foreach (var grant in grants)
            {
                await consentStore.RecordAsync(grant, cancellationToken).ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RevokeGrantsAsync(sessionId, grants, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await RevokeGrantsAsync(sessionId, grants, CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    public async Task<EllaiVoiceTranscriptionResult?> TranscribeAsync(
        string sessionId, EllaiVoiceCaptureMode mode, Stream audio, long? contentLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (string.IsNullOrWhiteSpace(sessionId)
            || (mode == EllaiVoiceCaptureMode.Recorded && !options.HasUploadAsrRoute)
            || (mode == EllaiVoiceCaptureMode.Live && !options.HasRealtimeAsrRoute)
            || contentLength is < 1 or > MaximumAudioBytes)
        {
            return null;
        }

        if (!await HasConsentAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        using var bounded = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await audio.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (bounded.Length + read > MaximumAudioBytes)
            {
                return null;
            }
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (bounded.Length == 0)
        {
            return null;
        }
        bounded.Position = 0;

        try
        {
            string transcript;
            if (mode == EllaiVoiceCaptureMode.Recorded)
            {
                if (bounded.Length < 44 || !IsWaveFile(bounded))
                {
                    return null;
                }
                bounded.Position = 0;
                var adapter = services.GetService<MetaAsrUploadAdapter>();
                if (adapter is null)
                {
                    return null;
                }
                var result = await adapter.TranscribeAsync(bounded, "ellai-capture.wav", "audio/wav", cancellationToken)
                    .ConfigureAwait(false);
                transcript = result.Transcript;
            }
            else
            {
                if (bounded.Length % 2 != 0)
                {
                    return null;
                }
                var adapter = services.GetService<MetaAsrRealtimeAdapter>();
                transcript = string.Empty;
                if (adapter is not null)
                {
                    try
                    {
                        var result = await adapter.TranscribePcmAsync(bounded, cancellationToken).ConfigureAwait(false);
                        transcript = string.Join(" ", result.FinalTranscripts.Where(text => !string.IsNullOrWhiteSpace(text)));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning("Ellai realtime transcription failed ({FailureType}); checking upload fallback.",
                            ex.GetType().Name);
                    }
                }

                if (string.IsNullOrWhiteSpace(transcript) && options.HasUploadAsrRoute)
                {
                    var uploadAdapter = services.GetService<MetaAsrUploadAdapter>();
                    if (uploadAdapter is not null && bounded.Length <= MaximumAudioBytes - 44)
                    {
                        logger?.LogInformation("Ellai realtime transcription returned no final words; using the configured upload route.");
                        var wave = CreateWavePayload(bounded);
                        try
                        {
                            using var waveStream = new MemoryStream(wave, writable: false);
                            var result = await uploadAdapter.TranscribeAsync(
                                waveStream, "ellai-live-fallback.wav", "audio/wav", cancellationToken)
                                .ConfigureAwait(false);
                            transcript = result.Transcript;
                        }
                        finally
                        {
                            Array.Clear(wave);
                        }
                    }
                }
            }

            transcript = transcript.Trim();
            return transcript.Length is > 0 and <= 8_000
                ? new EllaiVoiceTranscriptionResult(transcript)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning("Ellai {CaptureMode} transcription failed ({FailureType}).",
                mode, ex.GetType().Name);
            return null;
        }
    }

    public async Task<bool> HasConsentAsync(string sessionId, CancellationToken cancellationToken)
    {
        await consent.HydrateAsync(sessionId, consentStore, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return consent.IsGranted(sessionId, EllaiConsentDimension.Microphone,
                EllaiConsentPurposes.VoiceTranscription, AudioDataClass, now)
            && consent.IsGranted(sessionId, EllaiConsentDimension.Transcription,
                EllaiConsentPurposes.VoiceTranscription, AudioDataClass, now);
    }

    private async Task RevokeGrantsAsync(
        string sessionId, IReadOnlyList<EllaiConsentGrant> grants, CancellationToken cancellationToken)
    {
        foreach (var grant in grants)
        {
            consent.Revoke(grant.GrantId);
            try
            {
                await consentStore.SetRevokedAsync(sessionId, grant.GrantId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // In-memory grants are revoked even if durable compensation fails.
            }
        }
    }

    private static bool IsWaveFile(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 44)
        {
            return false;
        }
        Span<byte> header = stackalloc byte[12];
        stream.Position = 0;
        var read = stream.Read(header);
        stream.Position = 0;
        return read == header.Length
            && header[..4].SequenceEqual("RIFF"u8)
            && header[8..12].SequenceEqual("WAVE"u8);
    }

    private static byte[] CreateWavePayload(Stream pcm)
    {
        pcm.Position = 0;
        var samples = new byte[checked((int)pcm.Length)];
        pcm.ReadExactly(samples);

        var wave = new byte[44 + samples.Length];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4, 4), wave.Length - 8);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8, 8));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24, 4), 24_000);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28, 4), 48_000);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34, 2), 16);
        "data"u8.CopyTo(wave.AsSpan(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40, 4), samples.Length);
        samples.CopyTo(wave, 44);
        Array.Clear(samples);
        return wave;
    }
}
