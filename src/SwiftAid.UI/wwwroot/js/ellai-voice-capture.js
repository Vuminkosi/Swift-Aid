const activeCaptures = new Set();
const targetRate = 24000;
const chunkSamples = 2048;
const maximumDurationMs = 8 * 60 * 1000;
const noSpeechTimeoutMs = 60 * 1000;
const maximumQueuedChunks = 8;

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

export async function startCapture(dotnet, showLiveVoiceLevel = false, sessionId = "") {
    if (!isSupported()) {
        throw new Error("Microphone capture requires browser AudioWorklet support.");
    }

    await stopCapture();
    const stream = await navigator.mediaDevices.getUserMedia({
        audio: {
            channelCount: { ideal: 1 },
            echoCancellation: { ideal: true },
            noiseSuppression: { ideal: true },
            autoGainControl: { ideal: true }
        },
        video: false
    });

    let context;
    try {
        context = new AudioContext({ sampleRate: targetRate });
        await context.audioWorklet.addModule(new URL("./ellai-voice-worklet.js", import.meta.url));
        await context.resume();
    } catch (error) {
        for (const track of stream.getTracks()) track.stop();
        if (context) {
            try { await context.close(); } catch { }
        }
        throw error;
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
    const settings = stream.getAudioTracks()[0]?.getSettings?.() ?? {};
    const state = {
        stream, context, source, processor, sink, dotnet,
        showLiveVoiceLevel: showLiveVoiceLevel === true,
        sessionId,
        playbackSessionId: "",
        playbackSequence: 0,
        playbackId: "",
        echoCancellationEnabled: settings.echoCancellation === true,
        bargeInTimer: 0,
        bargeInSignaled: false,
        voiceLevel: 0,
        active: true,
        pending: [],
        pendingLength: 0,
        phase: 0,
        queue: Promise.resolve(),
        queuedChunks: 0,
        limitTimer: 0,
        noSpeechTimer: 0
    };

    source.connect(processor);
    processor.connect(sink);
    sink.connect(context.destination);
    processor.port.onmessage = event => processAudio(state, event.data);
    state.limitTimer = globalThis.setTimeout(() => {
        if (state.active) void dotnet.invokeMethodAsync("OnVoiceCaptureLimitReached");
    }, maximumDurationMs);
    if (state.showLiveVoiceLevel) {
        state.noSpeechTimer = globalThis.setTimeout(() => {
            if (!state.active) return;
            state.active = false;
            void dotnet.invokeMethodAsync("OnVoiceCaptureNoSpeechTimeout");
        }, noSpeechTimeoutMs);
    }

    activeCaptures.add(state);
    const processing = ["echoCancellation", "noiseSuppression", "autoGainControl"]
        .map(name => `${name === "autoGainControl" ? "automatic gain control" : name.replace(/[A-Z]/g, value => ` ${value.toLowerCase()}`)} ${settings[name] === true ? "on" : settings[name] === false ? "off" : "not reported"}`)
        .join(", ");
    return `Microphone active; ${processing}.`;
}

function processAudio(state, input) {
    if (!state.active || !(input instanceof Float32Array)) return;

    if (state.showLiveVoiceLevel) {
        updateLiveSpeakGlow(state, input);
        if (state.voiceLevel >= 0.025) globalThis.clearTimeout(state.noSpeechTimer);
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

    const bargeInCandidate = state.showLiveVoiceLevel
        && state.echoCancellationEnabled
        && state.playbackSessionId === state.sessionId
        && state.voiceLevel >= 0.12;
    if (!bargeInCandidate) {
        globalThis.clearTimeout(state.bargeInTimer);
        state.bargeInTimer = 0;
        state.bargeInSignaled = false;
    } else if (!state.bargeInTimer && !state.bargeInSignaled) {
        state.bargeInTimer = globalThis.setTimeout(() => {
            state.bargeInTimer = 0;
            if (!state.active || state.playbackSessionId !== state.sessionId) return;
            state.bargeInSignaled = true;
            void state.dotnet.invokeMethodAsync(
                "OnVoiceBargeInDetected", state.playbackSequence, state.playbackId).catch(() => { });
        }, 180);
    }

    for (const button of document.querySelectorAll('[data-ellai-live-speak="true"]')) {
        const visibleLevel = state.voiceLevel < 0.025 ? 0 : state.voiceLevel;
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

function enqueueChunk(state, samples) {
    if (!state.active) return;
    if (state.queuedChunks >= maximumQueuedChunks) {
        state.active = false;
        void state.dotnet.invokeMethodAsync("OnVoiceCaptureBackpressure");
        return;
    }

    state.queuedChunks++;
    state.queue = state.queue
        .then(() => state.dotnet.invokeMethodAsync("ReceiveVoicePcmChunk", samples))
        .catch(() => {
            state.active = false;
            globalThis.setTimeout(() => {
                void state.dotnet.invokeMethodAsync("OnVoiceCaptureBackpressure").catch(() => { });
            }, 0);
        })
        .finally(() => { state.queuedChunks--; });
}

export async function stopCapture() {
    const captures = [...activeCaptures];
    for (const state of captures) {
        state.active = false;
        clearLiveSpeakGlow();
        globalThis.clearTimeout(state.limitTimer);
        globalThis.clearTimeout(state.noSpeechTimer);
        globalThis.clearTimeout(state.bargeInTimer);
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
        activeCaptures.delete(state);
    }
}

export function setPlaybackState(isPlaying, sessionId, sequence, playbackId) {
    for (const state of activeCaptures) {
        if (state.sessionId !== sessionId) continue;
        state.playbackSessionId = isPlaying ? sessionId : "";
        state.playbackSequence = isPlaying ? sequence : 0;
        state.playbackId = isPlaying ? playbackId : "";
        state.bargeInSignaled = false;
        globalThis.clearTimeout(state.bargeInTimer);
        state.bargeInTimer = 0;
    }
}

function enqueueFinalChunk(state, samples) {
    state.queuedChunks++;
    state.queue = state.queue
        .then(() => state.dotnet.invokeMethodAsync("ReceiveVoicePcmChunk", samples))
        .finally(() => { state.queuedChunks--; });
}

export async function cancelCapture() {
    for (const state of activeCaptures) {
        state.pending.length = 0;
        state.pendingLength = 0;
    }
    await stopCapture();
}
