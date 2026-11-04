using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Intelligence.Meta;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using System.Security.Claims;

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
        CancellationToken cancellationToken, string? voiceCaptureId = null,
        long voiceCaptureOffsetSamples = 0, ClaimsPrincipal? submissionPrincipal = null,
        EllaiLanguagePreferences? speechPreferences = null)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (string.IsNullOrWhiteSpace(sessionId)
            || !Enum.IsDefined(mode)
            || (speechPreferences is not null && !speechPreferences.IsValid())
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

        if (mode == EllaiVoiceCaptureMode.Live)
        {
            var turnCoordinator = services.GetService<EllaiVoiceTurnCoordinator>();
            if (turnCoordinator is null)
            {
                logger?.LogWarning(
                    "Ellai live transcription is unavailable because the authoritative turn coordinator is not registered.");
                return null;
            }

            using var turn = turnCoordinator.BeginStream(sessionId, cancellationToken, voiceCaptureId,
                serverSubmission: submissionPrincipal is not null);
            using var admission = submissionPrincipal is null ? null
                : new EllaiVoiceTurnAdmission(services, sessionId, submissionPrincipal, turnCoordinator, speechPreferences);
            var turnOptions = services.GetService<EllaiVoiceTurnOptions>() ?? new EllaiVoiceTurnOptions();
            using var durationCts = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(turnOptions.MaximumTurnDurationMs));
            using var liveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                turn.CancellationToken, durationCts.Token);
            try
            {
                if (audio is EllaiVoiceWebSocketStream segmented)
                {
                    using var captureVad = new SileroVoiceActivityAudioStream(services.GetRequiredService<IVoiceActivityDetector>());
                    captureVad.SetSourceSampleOffset(voiceCaptureOffsetSamples);
                    var requestSequence = 0;
                    EllaiVoiceTranscriptionResult? latest = null;
                    while (await segmented.WaitForSegmentAsync(liveCancellation.Token).ConfigureAwait(false))
                    {
                        turnCoordinator.BeginTranscriptionRequest(turn);
                        latest = await TranscribeLiveStreamAsync(segmented, turn, segmented.SegmentStartPosition,
                            true, liveCancellation.Token, admission, $"segment-{++requestSequence}:", captureVad).ConfigureAwait(false);
                        if (!segmented.SegmentEnded && !segmented.SocketClosed) return null;
                    }
                    return latest ?? new EllaiVoiceTranscriptionResult(string.Empty, ServerSubmission: admission is not null);
                }
                return await TranscribeLiveStreamAsync(
                    audio, turn, voiceCaptureOffsetSamples, contentLength.HasValue || audio.CanSeek,
                    liveCancellation.Token, admission)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (durationCts.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
                && !turn.CancellationToken.IsCancellationRequested)
            {
                turn?.MarkRecovering();
                logger?.LogInformation("Ellai live voice turn reached its configured duration limit.");
                return null;
            }
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

            var captureIdentity = !string.IsNullOrWhiteSpace(voiceCaptureId) && voiceCaptureId.Length <= 64
                ? voiceCaptureId
                : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bounded));
            bounded.Position = 0;
            var utteranceId = "voice-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{sessionId}:{captureIdentity}")));
            var result = await adapter.TranscribeAsync(
                bounded, "ellai-capture.wav", "audio/wav", cancellationToken).ConfigureAwait(false);
            var transcript = result.Transcript.Trim();
            if (transcript.Length is not (> 0 and <= 8_000)) return null;
            if (submissionPrincipal is null) return new EllaiVoiceTranscriptionResult(transcript);
            using var admission = new EllaiVoiceTurnAdmission(services, sessionId, submissionPrincipal,
                speechPreferences: speechPreferences);
            var accepted = await admission.SubmitAsync(utteranceId, transcript).ConfigureAwait(false);
            return new EllaiVoiceTranscriptionResult(transcript, utteranceId, accepted, ServerSubmission: true);
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

    private async Task<EllaiVoiceTranscriptionResult?> TranscribeLiveStreamAsync(
        Stream audio,
        EllaiVoiceTurnCoordinator.VoiceStreamLease? turn,
        long voiceCaptureOffsetSamples,
        bool finiteInput,
        CancellationToken cancellationToken,
        EllaiVoiceTurnAdmission? admission, string providerRequestPrefix = "", SileroVoiceActivityAudioStream? captureVad = null)
    {
        using var capturedPcm = new MemoryStream();
        using var boundedAudio = new CapturingLimitReadStream(audio, capturedPcm, MaximumAudioBytes);
        try
        {
            var adapter = services.GetService<MetaAsrRealtimeAdapter>();
            var transcript = string.Empty;
            if (adapter is not null)
            {
                logger?.LogInformation("Voice ASR dispatch: session={SessionId} capture={CaptureId} mode={ProviderMode} finiteInput={FiniteInput}.",
                    turn?.SessionId, turn?.VoiceCaptureId, options.AsrMode, finiteInput);
                var finalizedTurns = new List<(string ProviderTurnId, string Text)>();
                var unscopedTurnSequence = 0L;
                var vadStream = captureVad ?? services.GetService<SileroVoiceActivityAudioStream>();
                if (captureVad is null) vadStream?.SetSourceSampleOffset(voiceCaptureOffsetSamples);
                Channel<VadChunk>? vadChunks = null;
                Task? vadWorker = null;
                if (vadStream is not null)
                {
                    vadChunks = Channel.CreateBounded<VadChunk>(new BoundedChannelOptions(8)
                    {
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleReader = true,
                        SingleWriter = true,
                        AllowSynchronousContinuations = false,
                    });
                    vadWorker = ProcessVadChunksAsync(vadChunks.Reader, vadStream, turn, cancellationToken, admission);
                }

                try
                {
                    await adapter.TranscribePcmAsync(
                        boundedAudio,
                        cancellationToken,
                        vadChunks is null
                            ? null
                            : (chunk, ct) => vadChunks.Writer.WriteAsync(new VadChunk(chunk.ToArray(), (audio as EllaiVoiceWebSocketStream)?.LastReadSamplePosition), ct),
                        (partial, _) =>
                        {
                            var providerTurnId = providerRequestPrefix + (partial.TurnId?.ToString(
                                System.Globalization.CultureInfo.InvariantCulture) ?? "unscoped");
                            turn?.ObservePartialTranscript(providerTurnId, partial.Text);
                            return ValueTask.CompletedTask;
                        },
                        async (finalTurn, _) =>
                        {
                            logger?.LogInformation("Voice provider final: session={SessionId} capture={CaptureId} providerTurn={ProviderTurnId} characters={Characters}.",
                                turn?.SessionId, turn?.VoiceCaptureId, finalTurn.TurnId, finalTurn.Text.Length);
                            var providerTurnId = providerRequestPrefix + (finalTurn.TurnId?.ToString(
                                System.Globalization.CultureInfo.InvariantCulture)
                                ?? $"unscoped:{Interlocked.Increment(ref unscopedTurnSequence)}");
                            var accepted = turn?.AcceptFinalTranscript(providerTurnId, finalTurn.Text);
                            if (turn is null || accepted is not null)
                            {
                                finalizedTurns.Add((providerTurnId, accepted?.Text ?? finalTurn.Text.Trim()));
                            }
                            if (admission is not null) await admission.SubmitCompletedAsync().ConfigureAwait(false);
                        })
                        .ConfigureAwait(false);
                    transcript = string.Join(" ", finalizedTurns.Select(turn => turn.Text));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(
                        "Ellai realtime transcription failed: session={SessionId} capture={CaptureId} failure={FailureType}; checking upload fallback.",
                        turn?.SessionId, turn?.VoiceCaptureId, ex.GetType().Name);
                    if ((!finiteInput || audio is EllaiVoiceWebSocketStream) && !boundedAudio.InputCompleted)
                    {
                        // The client owns the still-running microphone and its recovery copy.
                        // Waiting for EOF here hides provider failure for the entire conversation.
                        turn?.MarkRecovering();
                        return null;
                    }
                }
                finally
                {
                    vadChunks?.Writer.TryComplete();
                    if (vadWorker is not null)
                    {
                        await vadWorker.ConfigureAwait(false);
                    }
                    if (captureVad is null) vadStream?.Dispose();
                    if (admission is not null) await admission.SubmitCompletedAsync().ConfigureAwait(false);
                }
            }

            // A provider connection can fail before it consumes the live body.
            // Keep draining into the bounded transient capture so fallback can
            // run and the client-side producer cannot remain blocked.
            var drainBuffer = new byte[32 * 1024];
            while (await boundedAudio.ReadAsync(drainBuffer, cancellationToken).ConfigureAwait(false) > 0)
            {
            }

            if (boundedAudio.LimitExceeded)
            {
                return null;
            }

            if ((capturedPcm.Length & 1) != 0)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(transcript) && options.HasUploadAsrRoute
                && capturedPcm.Length > 0 && capturedPcm.Length <= MaximumAudioBytes - 44)
            {
                logger?.LogInformation(
                    "Ellai realtime transcription returned no final words; using the configured upload route.");
                var wave = CreateWavePayload(capturedPcm);
                try
                {
                    var uploadAdapter = services.GetService<MetaAsrUploadAdapter>();
                    if (uploadAdapter is not null)
                    {
                        using var waveStream = new MemoryStream(wave, writable: false);
                        var result = await uploadAdapter.TranscribeAsync(
                            waveStream, "ellai-live-fallback.wav", "audio/wav", cancellationToken)
                            .ConfigureAwait(false);
                        var fallbackTranscript = result.Transcript.Trim();
                        var fallbackProviderTurnId = $"{result.SessionId}:upload-fallback";
                        var acceptedFallback = turn?.AcceptFinalTranscript(
                            fallbackProviderTurnId, fallbackTranscript);
                        if (admission is not null) await admission.SubmitCompletedAsync().ConfigureAwait(false);
                        if (turn is null || acceptedFallback is not null)
                        {
                            var fallbackWasTurnCompleted = turn is not null
                                && services.GetService<EllaiVoiceTurnCoordinator>()
                                    ?.GetCompletedTurns(turn.SessionId)
                                    .Any(completed => string.Equals(
                                        completed.ProviderTurnId, fallbackProviderTurnId,
                                        StringComparison.Ordinal)) == true;
                            transcript = fallbackWasTurnCompleted
                                ? string.Empty
                                : acceptedFallback?.Text ?? fallbackTranscript;
                        }
                    }
                }
                finally
                {
                    Array.Clear(wave);
                }
            }

            if (turn is not null)
            {
                // Provider-final text is still provisional until Gateway has
                // confirmed the acoustic endpoint. Return only turns bound to
                // this capture and stream epoch. Updated clients receive the
                // server admission result; legacy clients still submit by ID.
                var completedTurns = services.GetService<EllaiVoiceTurnCoordinator>()
                    ?.GetCompletedTurns(turn.SessionId)
                    .Where(completed => completed.StreamEpoch == turn.Epoch
                        && string.Equals(completed.VoiceCaptureId, turn.VoiceCaptureId, StringComparison.Ordinal))
                    .Select(completed => completed.Transcript)
                    ?? [];
                transcript = string.Join(" ", completedTurns);
            }

            transcript = transcript.Trim();
            if (transcript.Length == 0)
            {
                turn?.MarkRecovering();
            }
            return transcript.Length is > 0 and <= 8_000
                ? new EllaiVoiceTranscriptionResult(transcript, ServerSubmission: admission is not null)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            turn?.MarkRecovering();
            logger?.LogWarning("Ellai live transcription failed ({FailureType}).", ex.GetType().Name);
            return null;
        }
        finally
        {
            if (capturedPcm.TryGetBuffer(out var buffer))
            {
                Array.Clear(buffer.Array!, buffer.Offset, buffer.Count);
            }
        }
    }

    private sealed record VadChunk(byte[] Pcm, long? Position);

    private async Task ProcessVadChunksAsync(
        ChannelReader<VadChunk> chunks,
        SileroVoiceActivityAudioStream vadStream,
        EllaiVoiceTurnCoordinator.VoiceStreamLease? turn,
        CancellationToken cancellationToken,
        EllaiVoiceTurnAdmission? admission)
    {
        var vadEnabled = true;
        EllaiVoiceTurnState? lastState = null;
        var lastDiagnostic = System.Diagnostics.Stopwatch.GetTimestamp();
        var turnCoordinator = turn is null ? null : services.GetService<EllaiVoiceTurnCoordinator>();
        try
        {
            await foreach (var chunk in chunks.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (vadEnabled)
                    {
                        if (chunk.Position is { } position) await vadStream.AdvanceToAsync(position, cancellationToken).ConfigureAwait(false);
                        var activities = await vadStream.AppendPcm16Async(chunk.Pcm, 24_000, cancellationToken)
                            .ConfigureAwait(false);
                        if (turn is not null && turnCoordinator is not null)
                        {
                            var playbackCoordinator = services.GetService<EllaiResponseSpeechCoordinator>();
                            foreach (var activity in activities)
                            {
                                var bargeIn = turnCoordinator.ObserveAcousticFrame(
                                    turn, activity, TimeSpan.FromMilliseconds(32));
                                var snapshot = turnCoordinator.GetSnapshot(turn.SessionId);
                                if (snapshot.State != lastState || System.Diagnostics.Stopwatch.GetElapsedTime(lastDiagnostic) >= TimeSpan.FromSeconds(5))
                                {
                                    lastState = snapshot.State;
                                    lastDiagnostic = System.Diagnostics.Stopwatch.GetTimestamp();
                                    logger?.LogInformation("Voice activity: session={SessionId} capture={CaptureId} epoch={Epoch} state={State} frame={Frame} speechMs={SpeechMs} silenceMs={SilenceMs}.",
                                        turn.SessionId, turn.VoiceCaptureId, turn.Epoch, snapshot.State, activity.Sequence,
                                        snapshot.ConfirmedSpeech.TotalMilliseconds, snapshot.Silence.TotalMilliseconds);
                                }
                                if (bargeIn is not null)
                                {
                                    playbackCoordinator?.Interrupt(
                                        bargeIn.SessionId, bargeIn.ResponseSequence, bargeIn.PlaybackId);
                                    services.GetService<EllaiSessionResponseStore>()?.InterruptProgress(
                                        bargeIn.SessionId, bargeIn.ResponseSequence);
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    vadEnabled = false;
                    logger?.LogWarning("Ellai voice activity processing failed ({FailureType}); continuing transcription.",
                        ex.GetType().Name);
                }
                finally
                {
                    Array.Clear(chunk.Pcm);
                }
                if (admission is not null) await admission.SubmitCompletedAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

    private sealed class CapturingLimitReadStream(Stream source, MemoryStream capture, int maximumBytes) : Stream
    {
        private int _bytesRead;
        private bool _overflowChecked;

        public bool LimitExceeded { get; private set; }
        public bool InputCompleted { get; private set; }

        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var allowed = GetAllowedReadCount(count);
            if (allowed == 0)
            {
                return CheckEndOfInput();
            }

            var read = source.Read(buffer, offset, allowed);
            Capture(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var allowed = GetAllowedReadCount(buffer.Length);
            if (allowed == 0)
            {
                return await CheckEndOfInputAsync(cancellationToken).ConfigureAwait(false);
            }

            var read = await source.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
            if (read == 0 && allowed > 0) InputCompleted = true;
            Capture(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int GetAllowedReadCount(int requested)
        {
            if (_bytesRead == maximumBytes)
            {
                return 0;
            }

            return Math.Min(requested, maximumBytes - _bytesRead);
        }

        private int CheckEndOfInput()
        {
            if (_overflowChecked)
            {
                return 0;
            }

            _overflowChecked = true;
            Span<byte> extra = stackalloc byte[1];
            if (source.Read(extra) != 0)
            {
                LimitExceeded = true;
                throw new InvalidDataException("Live voice audio exceeded the configured size limit.");
            }
            return 0;
        }

        private async ValueTask<int> CheckEndOfInputAsync(CancellationToken cancellationToken)
        {
            if (_overflowChecked)
            {
                return 0;
            }

            _overflowChecked = true;
            var extra = new byte[1];
            if (await source.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
            {
                LimitExceeded = true;
                throw new InvalidDataException("Live voice audio exceeded the configured size limit.");
            }
            return 0;
        }

        private void Capture(ReadOnlySpan<byte> bytes)
        {
            _bytesRead += bytes.Length;
            capture.Write(bytes);
        }
    }
}
