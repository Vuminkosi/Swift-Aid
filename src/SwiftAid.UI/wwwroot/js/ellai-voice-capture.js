const activeCaptures = new Set();
const targetRate = 24000;
const chunkSamples = 2048;
const maximumDurationMs = 8 * 60 * 1000;
const noSpeechTimeoutMs = 60 * 1000;
const endpointSilenceMs = 1200;
const speechOnsetMs = 180;
const minimumSpeechMs = 300;
const speechRmsThreshold = 0.018;

export function isSupported() {
    return Boolean(navigator.mediaDevices?.getUserMedia
        && (globalThis.AudioContext || globalThis.webkitAudioContext));
}

export async function startCapture(dotnet, showLiveVoiceLevel = false) {
    if (!isSupported()) {
        throw new Error("Microphone capture is not supported in this browser or app host.");
    }

    await stopCapture();
    const stream = await navigator.mediaDevices.getUserMedia({
        audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true }
    });
    const AudioContextType = globalThis.AudioContext || globalThis.webkitAudioContext;
    let context;
    try {
        context = new AudioContextType({ sampleRate: targetRate });
    } catch {
        context = new AudioContextType();
    }

    const state = {
        stream,
        context,
        source: context.createMediaStreamSource(stream),
        processor: context.createScriptProcessor(4096, 1, 1),
        sink: context.createGain(),
        dotnet,
        showLiveVoiceLevel: showLiveVoiceLevel === true,
        voiceLevel: 0,
        noiseFloor: 0.004,
        speechOnsetAt: 0,
        speechStartedAt: 0,
        lastSpeechAt: 0,
        endpointNotified: false,
        active: true,
        pending: [],
        pendingLength: 0,
        phase: 0,
        queue: Promise.resolve(),
        limitTimer: 0,
        noSpeechTimer: 0,
    };
    state.sink.gain.value = 0;
    state.sink.connect(context.destination);
    state.source.connect(state.processor);
    state.processor.connect(state.sink);
    state.processor.onaudioprocess = event => processAudio(state, event.inputBuffer.getChannelData(0));
    state.limitTimer = globalThis.setTimeout(() => {
        void dotnet.invokeMethodAsync("OnVoiceCaptureLimitReached");
    }, maximumDurationMs);
    if (state.showLiveVoiceLevel) {
        state.noSpeechTimer = globalThis.setTimeout(() => {
            if (!state.active || state.speechStartedAt > 0 || state.endpointNotified) return;
            state.endpointNotified = true;
            void dotnet.invokeMethodAsync("OnVoiceCaptureNoSpeechTimeout");
        }, noSpeechTimeoutMs);
    }
    activeCaptures.add(state);
    await context.resume();
}

function processAudio(state, input) {
    if (!state.active) return;
    const now = globalThis.performance.now();
    if (state.showLiveVoiceLevel) {
        updateLiveSpeakGlow(state, input);
        detectLiveVoiceEndpoint(state, input, now);
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

    for (const sample of output) {
        state.pending.push(sample);
    }
    state.pendingLength += output.length;
    while (state.pendingLength >= chunkSamples) {
        const chunk = state.pending.splice(0, chunkSamples);
        state.pendingLength -= chunkSamples;
        enqueueChunk(state, chunk);
    }
}

function detectLiveVoiceEndpoint(state, input, now) {
    if (state.endpointNotified) return;

    let squaredTotal = 0;
    for (let index = 0; index < input.length; index++) {
        squaredTotal += input[index] * input[index];
    }
    const rms = Math.sqrt(squaredTotal / Math.max(1, input.length));
    const threshold = Math.max(speechRmsThreshold, state.noiseFloor * 2.8);

    if (rms >= threshold) {
        if (state.speechOnsetAt === 0) state.speechOnsetAt = now;
        state.lastSpeechAt = now;
        if (state.speechStartedAt === 0 && now - state.speechOnsetAt >= speechOnsetMs) {
            state.speechStartedAt = state.speechOnsetAt;
            globalThis.clearTimeout(state.noSpeechTimer);
        }
        return;
    }

    state.speechOnsetAt = 0;
    if (rms < speechRmsThreshold) {
        state.noiseFloor = Math.max(0.002, Math.min(0.012, state.noiseFloor * 0.96 + rms * 0.04));
    }

    const speechDurationMs = state.speechStartedAt === 0 ? 0 : state.lastSpeechAt - state.speechStartedAt;
    if (speechDurationMs >= minimumSpeechMs && now - state.lastSpeechAt >= endpointSilenceMs) {
        state.endpointNotified = true;
        void state.dotnet.invokeMethodAsync("OnVoiceTurnEnded");
    }
}

function updateLiveSpeakGlow(state, input) {
    let squaredTotal = 0;
    for (let index = 0; index < input.length; index++) {
        squaredTotal += input[index] * input[index];
    }
    const rms = Math.sqrt(squaredTotal / Math.max(1, input.length));
    const normalizedLevel = Math.max(0, Math.min(1, (rms - 0.008) / 0.16));
    state.voiceLevel = state.voiceLevel * 0.58 + normalizedLevel * 0.42;

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
    state.queue = state.queue.then(() => state.dotnet.invokeMethodAsync("ReceiveVoicePcmChunk", samples));
    state.queue.catch(() => { state.active = false; });
}

export async function stopCapture() {
    const captures = [...activeCaptures];
    for (const state of captures) {
        state.active = false;
        clearLiveSpeakGlow();
        globalThis.clearTimeout(state.limitTimer);
        globalThis.clearTimeout(state.noSpeechTimer);
        state.processor.onaudioprocess = null;
        state.source.disconnect();
        state.processor.disconnect();
        state.sink.disconnect();
        if (state.pendingLength > 0) {
            enqueueChunk(state, state.pending.splice(0, state.pendingLength));
            state.pendingLength = 0;
        }
        for (const track of state.stream.getTracks()) track.stop();
        try { await state.context.close(); } catch { }
        await state.queue;
        activeCaptures.delete(state);
    }
}

export async function cancelCapture() {
    for (const state of activeCaptures) {
        state.pending.length = 0;
        state.pendingLength = 0;
    }
    await stopCapture();
}
