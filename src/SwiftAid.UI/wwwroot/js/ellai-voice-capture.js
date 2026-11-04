import { stopResponse } from "./ellai-audio-playback.js";
import { SilenceGate, protocol, createSilero } from "./ellai-segmented-audio.js";
const activeCaptures = new Set();
const targetRate = 24000;
const chunkSamples = 768;
const maximumDurationMs = 8 * 60 * 1000;
const maximumQueuedChunks = 64;
const maximumRecoveryBytes = 32 * 1024 * 1024;

export function isSupported() {
    return Boolean(navigator.mediaDevices?.getUserMedia
        && globalThis.AudioContext
        && globalThis.AudioWorkletNode);
}

export function isOnline() {
    return globalThis.navigator?.onLine !== false;
}

export function startConnectivityMonitoring(dotnet) {
    const notify = () => {
        void dotnet.invokeMethodAsync("OnVoiceConnectivityChanged", isOnline()).catch(() => { });
    };
    globalThis.addEventListener("online", notify);
    globalThis.addEventListener("offline", notify);
    return {
        dispose() {
            globalThis.removeEventListener("online", notify);
            globalThis.removeEventListener("offline", notify);
        }
    };
}

export async function startCapture(dotnet, showLiveVoiceLevel = false, sessionId = "", voiceSocketUrl = "") {
    if (!isSupported()) {
        throw new Error("Microphone capture requires browser AudioWorklet support.");
    }

    await stopCapture();
    let voiceSocket = null;
    let stream;
    try {
        stream = await navigator.mediaDevices.getUserMedia({
            audio: {
                channelCount: { ideal: 1 },
                echoCancellation: { ideal: true },
                noiseSuppression: { ideal: true },
                autoGainControl: { ideal: true }
            },
            video: false
        });
    } catch (error) {
        if (voiceSocket) await closeVoiceSocket(voiceSocket);
        throw error;
    }

    let context;
    try {
        context = new AudioContext({ sampleRate: targetRate });
        await context.audioWorklet.addModule(new URL("./ellai-voice-worklet.js", import.meta.url));
        await context.resume();
    } catch (error) {
        for (const track of stream.getTracks()) track.stop();
        if (voiceSocket) await closeVoiceSocket(voiceSocket);
        if (context) {
            try { await context.close(); } catch { }
        }
        throw error;
    }

    if (voiceSocketUrl) {
        try { voiceSocket = await openVoiceSocket(voiceSocketUrl); }
        catch { /* Keep capture available for bounded reconnect recovery. */ }
    }
    let silero = null;
    if (voiceSocketUrl) {
        try { silero = await createSilero(); }
        catch (error) {
            for (const track of stream.getTracks()) track.stop();
            if (voiceSocket) await closeVoiceSocket(voiceSocket);
            await context.close();
            throw error;
        }
    }
    const source = context.createMediaStreamSource(stream);
    const processor = new AudioWorkletNode(context, "ellai-voice-capture", {
        numberOfInputs: 1,
        numberOfOutputs: 1,
        channelCount: 1,
        channelCountMode: "explicit",
        channelInterpretation: "speakers"
    });
    const sink = context.createGain();
    sink.gain.value = 0;
    const track = stream.getAudioTracks()[0];
    const settings = track?.getSettings?.() ?? {};
    const echoCancellationEnabled = typeof settings.echoCancellation === "boolean"
        ? settings.echoCancellation
        : settings.echoCancellation === "all" ? true
            : settings.echoCancellation === "remote-only" ? false : null;
    await dotnet.invokeMethodAsync(
        "OnVoiceAudioProcessingChanged",
        {
            platform: "Web",
            echoCancellationEnabled,
            noiseSuppressionEnabled: typeof settings.noiseSuppression === "boolean" ? settings.noiseSuppression : null,
            automaticGainControlEnabled: typeof settings.autoGainControl === "boolean" ? settings.autoGainControl : null,
            captureActive: true
        });
    const state = {
        stream, context, source, processor, sink, dotnet,
        voiceSocket, silero,
        gate: voiceSocketUrl ? new SilenceGate(Date.now(), 0, () => {
            if (echoCancellationEnabled === true) stopResponse(sessionId);
            void dotnet.invokeMethodAsync("OnClientSileroSpeech", new URL(voiceSocketUrl).searchParams.get("voiceCaptureId")).catch(() => {});
        }) : null,
        lastHeartbeatPosition: 0,
        usesVoiceSocket: Boolean(voiceSocketUrl),
        transportRecovering: false,
        recoveryFrames: [],
        recoveryBytes: 0,
        unmirroredFrames: [],
        producedBytes: 0,
        showLiveVoiceLevel: showLiveVoiceLevel === true,
        sessionId,
        voiceLevel: 0,
        active: true,
        pending: [],
        pendingLength: 0,
        phase: 0,
        queue: Promise.resolve(),
        queuedChunks: 0,
        limitTimer: 0,
        cancelled: false
    };

    source.connect(processor);
    processor.connect(sink);
    sink.connect(context.destination);
    processor.port.onmessage = event => processAudio(state, event.data);
    state.limitTimer = globalThis.setTimeout(() => {
        if (state.active) void dotnet.invokeMethodAsync("OnVoiceCaptureLimitReached");
    }, maximumDurationMs);
    activeCaptures.add(state);
    if (state.usesVoiceSocket && state.voiceSocket?.readyState !== WebSocket.OPEN) beginTransportRecovery(state);
    const formatState = value => value === true || value === "all"
        ? "on"
        : value === false ? "off" : value ?? "not reported";
    return `Web audio processing: echo cancellation ${formatState(settings.echoCancellation)}, noise suppression ${formatState(settings.noiseSuppression)}, automatic gain control ${formatState(settings.autoGainControl)}.`;
}

function processAudio(state, input) {
    if (!state.active || !(input instanceof Float32Array)) return;

    if (state.showLiveVoiceLevel) {
        updateLiveSpeakGlow(state, input);
    }

    const ratio = state.context.sampleRate / targetRate;
    const output = [];
    let offset = state.phase;
    for (; offset < input.length; offset += ratio) {
        const index = Math.floor(offset);
        const next = Math.min(index + 1, input.length - 1);
        const fraction = offset - index;
        const sample = input[index] * (1 - fraction) + input[next] * fraction;
        output.push(Math.max(-32768, Math.min(32767,
            Math.round(sample < 0 ? sample * 32768 : sample * 32767))));
    }
    state.phase = offset - input.length;

    for (const sample of output) state.pending.push(sample);
    state.pendingLength += output.length;
    while (state.pendingLength >= chunkSamples) {
        enqueueChunk(state, state.pending.splice(0, chunkSamples));
        state.pendingLength -= chunkSamples;
    }
}

function updateLiveSpeakGlow(state, input) {
    let squaredTotal = 0;
    for (let index = 0; index < input.length; index++) squaredTotal += input[index] * input[index];
    const rms = Math.sqrt(squaredTotal / Math.max(1, input.length));
    const normalizedLevel = Math.max(0, Math.min(1, (rms - 0.008) / 0.16));
    state.voiceLevel = state.voiceLevel * 0.58 + normalizedLevel * 0.42;

    renderLiveSpeakGlow(state.voiceLevel);
}

export function setNativeVoiceLevel(level) {
    renderLiveSpeakGlow(Math.max(0, Math.min(1, level)));
}

function renderLiveSpeakGlow(level) {
    for (const button of document.querySelectorAll('[data-ellai-live-speak="true"]')) {
        const visibleLevel = level < 0.025 ? 0 : level;
        button.style.setProperty("--ellai-voice-glow-radius", visibleLevel === 0 ? "0px" : `${Math.round(4 + visibleLevel * 22)}px`);
        button.style.setProperty("--ellai-voice-glow-opacity", visibleLevel === 0 ? "0%" : `${Math.round(18 + visibleLevel * 52)}%`);
    }
}

function clearLiveSpeakGlow() {
    for (const button of document.querySelectorAll("[data-ellai-live-speak]")) {
        button.style.removeProperty("--ellai-voice-glow-radius");
        button.style.removeProperty("--ellai-voice-glow-opacity");
    }
}

function beginTransportRecovery(state) {
    if (state.transportRecovering || !state.active) return;
    state.transportRecovering = true;
    state.recoveryFrames = [...state.unmirroredFrames];
    state.recoveryBytes = state.recoveryFrames.reduce((sum, frame) => sum + frame.bytes.byteLength, 0);
    void state.dotnet.invokeMethodAsync("OnVoiceCaptureBackpressure").catch(() => { });
}

function enqueueChunk(state, samples) {
    if (!state.active) return;
    if (state.queuedChunks >= maximumQueuedChunks || state.recoveryBytes >= maximumRecoveryBytes) {
        // A finite buffer cannot hide sustained loss. End explicitly, preserving the captured copy.
        state.active = false;
        void state.dotnet.invokeMethodAsync("OnVoiceCaptureLimitReached").catch(() => { });
        return;
    }

    let frame = null;
    if (state.usesVoiceSocket) {
        frame = { start: state.producedBytes, bytes: encodePcm16(samples) };
        state.producedBytes += frame.bytes.byteLength;
        state.unmirroredFrames.push(frame);
        if (state.transportRecovering) {
            state.recoveryFrames.push(frame);
            state.recoveryBytes += frame.bytes.byteLength;
        }
    }

    state.queuedChunks++;
    state.queue = state.queue
        .then(async () => {
            if (frame && !state.transportRecovering) {
                if (state.voiceSocket?.readyState !== WebSocket.OPEN || state.voiceSocket.bufferedAmount > 65536) beginTransportRecovery(state);
                else {
                    const probability = await state.silero.analyze(samples);
                    try { for (const packet of state.gate.push(new Uint8Array(frame.bytes), probability)) state.voiceSocket.send(packet); }
                    catch { beginTransportRecovery(state); }
                    if (state.gate.position - state.lastHeartbeatPosition >= 120000) {
                        state.voiceSocket.send(state.gate.heartbeat());
                        state.lastHeartbeatPosition = state.gate.position;
                    }
                }
            }
            await state.dotnet.invokeMethodAsync("ReceiveVoicePcmChunk", samples);
        })
        .catch(() => {
            state.active = false;
            // Interop failure is observable; do not silently discard the recording.
            void state.dotnet.invokeMethodAsync("OnVoiceCaptureLimitReached").catch(() => { });
        })
        .finally(() => {
            state.queuedChunks--;
            if (frame) state.unmirroredFrames = state.unmirroredFrames.filter(item => item !== frame);
        });
}

export async function stopCapture() {
    const captures = [...activeCaptures];
    for (const state of captures) {
        state.active = false;
        clearLiveSpeakGlow();
        globalThis.clearTimeout(state.limitTimer);
        state.processor.port.onmessage = null;
        state.source.disconnect();
        state.processor.disconnect();
        state.sink.disconnect();
        if (state.pendingLength > 0) {
            enqueueFinalChunk(state, state.pending.splice(0, state.pendingLength));
            state.pendingLength = 0;
        }
        for (const track of state.stream.getTracks()) track.stop();
        try { await state.context.close(); } catch { }
        await state.queue;
        if (state.voiceSocket) {
            if (!state.cancelled && state.voiceSocket.readyState === WebSocket.OPEN) {
                state.voiceSocket.send(state.gate.heartbeat());
            }
            await closeVoiceSocket(state.voiceSocket);
            state.voiceSocket = null;
        }
        await state.silero?.destroy();
        if (state.gate) console.info("Ellai voice transport", { audioBytes: state.gate.audioBytes, suppressedSamples: state.gate.suppressedSamples });
        state.gate?.dispose();
        activeCaptures.delete(state);
    }
}

function enqueueFinalChunk(state, samples) {
    state.queue = state.queue.then(async () => {
        if (state.voiceSocket?.readyState === WebSocket.OPEN && !state.transportRecovering) {
            for (const packet of state.gate.push(new Uint8Array(encodePcm16(samples)), 0)) state.voiceSocket.send(packet);
        }
        await state.dotnet.invokeMethodAsync("ReceiveVoicePcmChunk", samples);
    });
}

export async function cancelCapture() {
    for (const state of activeCaptures) {
        state.cancelled = true;
        state.pending.length = 0;
        state.pendingLength = 0;
    }
    await stopCapture();
}

export async function closeVoiceStream() {
    for (const state of activeCaptures) {
        state.cancelled = true;
        if (state.voiceSocket) {
            await closeVoiceSocket(state.voiceSocket);
            state.voiceSocket = null;
        }
    }
}

export async function reconnectVoiceStream(voiceSocketUrl, replayPcm) {
    const state = [...activeCaptures].find(capture => capture.active);
    if (!state || !voiceSocketUrl || !(replayPcm instanceof Uint8Array)) return false;
    beginTransportRecovery(state);
    const previous = state.voiceSocket;
    if (previous) {
        previous.onclose = null;
        try { previous.close(1000, "Replacing recovered voice stream."); } catch { }
    }

    const socket = await openVoiceSocket(voiceSocketUrl);
    await state.queue;
    state.voiceSocket = socket;
    await state.silero.destroy();
    state.silero = await createSilero();
    const speech = state.gate.onSpeech;
    state.gate.dispose();
    state.gate = new SilenceGate(Date.now(), 0, speech);
    state.lastHeartbeatPosition = 0;
    let pendingReplay = new Uint8Array();
    const deadline = Date.now() + 10000;
    async function send(bytes) {
        bytes = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
        while (socket.bufferedAmount > 65536 && socket.readyState === WebSocket.OPEN && state.active) {
            if (Date.now() >= deadline) throw new Error("Voice recovery buffer did not drain.");
            await new Promise(resolve => globalThis.setTimeout(resolve, 20));
        }
        if (!state.active || socket.readyState !== WebSocket.OPEN) throw new Error("Voice recovery connection closed.");
        const combined = new Uint8Array(pendingReplay.length + bytes.length);
        combined.set(pendingReplay); combined.set(bytes, pendingReplay.length);
        let offset = 0;
        for (; offset + 1536 <= combined.length; offset += 1536) {
            const pcm = combined.slice(offset, offset + 1536), view = new DataView(pcm.buffer);
            const samples = Array.from({ length: 768 }, (_, i) => view.getInt16(i * 2, true));
            const probability = await state.silero.analyze(samples);
            for (const packet of state.gate.push(pcm, probability)) socket.send(packet);
        }
        pendingReplay = combined.slice(offset);
    }
    const maximumChunkBytes = 8192;
    for (let offset = 0; offset < replayPcm.byteLength; offset += maximumChunkBytes) {
        await send(replayPcm.slice(offset, Math.min(offset + maximumChunkBytes, replayPcm.byteLength)));
    }
    // Audio continues while reconnecting. Absolute byte offsets reconcile the JS tail with
    // the .NET snapshot, including frames awaiting interop when the connection failed.
    let sentThrough = replayPcm.byteLength;
    while (state.recoveryFrames.length > 0) {
        const frame = state.recoveryFrames.shift();
        state.recoveryBytes -= frame.bytes.byteLength;
        const skip = Math.max(0, sentThrough - frame.start);
        if (skip < frame.bytes.byteLength) {
            await send(frame.bytes.slice(skip));
            sentThrough = frame.start + frame.bytes.byteLength;
        }
    }
    if (pendingReplay.length !== 0) throw new Error("Incomplete capture recovery frame.");
    state.transportRecovering = false;
    return true;
}

function openVoiceSocket(url) {
    return new Promise((resolve, reject) => {
        const socket = new WebSocket(url, protocol);
        socket.binaryType = "arraybuffer";
        const timeout = globalThis.setTimeout(() => {
            socket.close();
            reject(new Error("Voice stream connection timed out."));
        }, 10000);
        socket.onopen = () => {
            globalThis.clearTimeout(timeout);
            if (socket.protocol !== protocol) { socket.close(); reject(new Error("Unsupported voice protocol.")); return; }
            resolve(socket);
        };
        socket.onerror = () => {
            globalThis.clearTimeout(timeout);
            reject(new Error("Voice stream connection failed."));
        };
        socket.onclose = event => {
            globalThis.clearTimeout(timeout);
            const capture = [...activeCaptures].find(item => item.voiceSocket === socket);
            if (capture) {
                if (capture.active) beginTransportRecovery(capture);
                void capture.dotnet.invokeMethodAsync("OnVoiceWebSocketClosed", event.code, event.wasClean)
                    .catch(() => { });
            }
        };
    });
}

function closeVoiceSocket(socket) {
    return new Promise(resolve => {
        if (socket.readyState === WebSocket.CLOSED) {
            resolve();
            return;
        }
        const timeout = globalThis.setTimeout(resolve, 2000);
        socket.addEventListener("close", () => {
            globalThis.clearTimeout(timeout);
            resolve();
        }, { once: true });
        try {
            if (socket.readyState === WebSocket.OPEN) socket.close(1000, "Capture stopped.");
            else if (socket.readyState === WebSocket.CONNECTING) socket.close();
        } catch {
            globalThis.clearTimeout(timeout);
            resolve();
        }
    });
}

function encodePcm16(samples) {
    const bytes = new Uint8Array(samples.length * 2);
    const view = new DataView(bytes.buffer);
    for (let index = 0; index < samples.length; index++) {
        view.setInt16(index * 2, samples[index], true);
    }
    return bytes.buffer;
}
