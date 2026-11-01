const activePlayback = new Map();
const audioType = "application/vnd.swiftaid.pcm";
const maximumAudioBytes = 3_840_000;
let sharedAudioContext = null;
let audioUnlockAttached = false;

function getAudioContext() {
    const AudioContextType = globalThis.AudioContext || globalThis.webkitAudioContext;
    if (!AudioContextType) {
        throw new Error("This browser cannot play Ellai audio.");
    }

    sharedAudioContext ??= new AudioContextType();
    return sharedAudioContext;
}

function unlockAudioFromGesture(event) {
    const target = event.target;
    if (!(target instanceof Element) || !target.closest("[data-ellai-audio-unlock]")) {
        return;
    }

    try {
        const context = getAudioContext();
        if (context.state !== "running") {
            void context.resume().catch(() => {});
        }
    } catch {
        // Playback reports a useful error when a later audio action is attempted.
    }
}

export function initializeAudioUnlock() {
    if (audioUnlockAttached || typeof document === "undefined") {
        return;
    }

    document.addEventListener("pointerdown", unlockAudioFromGesture, true);
    document.addEventListener("keydown", unlockAudioFromGesture, true);
    document.addEventListener("click", unlockAudioFromGesture, true);
    audioUnlockAttached = true;
}

export async function disposeAudioPlayback() {
    document.removeEventListener("pointerdown", unlockAudioFromGesture, true);
    document.removeEventListener("keydown", unlockAudioFromGesture, true);
    document.removeEventListener("click", unlockAudioFromGesture, true);
    audioUnlockAttached = false;

    for (const record of activePlayback.values()) {
        stopRecord(record);
    }
    activePlayback.clear();

    const context = sharedAudioContext;
    sharedAudioContext = null;
    await context?.close();
}

function makeUrl(sessionId, sequence, guest, purpose, playbackId) {
    const route = guest ? "api/v1/ellai/guest" : "api/v1/ellai";
    const audioRoute = purpose === "live-activation"
        ? "voice/greeting/audio"
        : `responses/${sequence}/audio`;
    const playback = purpose === null && playbackId
        ? `?playbackId=${encodeURIComponent(playbackId)}`
        : "";
    return `/${route}/sessions/${encodeURIComponent(sessionId)}/${audioRoute}${playback}`;
}

function stopRecord(record) {
    record.controller.abort();
    for (const source of record.sources) {
        try {
            source.stop();
        } catch {
            // A source may already have ended.
        }
    }
}

export function stopResponse(sessionId) {
    const record = activePlayback.get(sessionId);
    if (record) {
        activePlayback.delete(sessionId);
        stopRecord(record);
    }
}

export async function playResponse(sessionId, sequence, playbackId) {
    return playPcm(sessionId, sequence, null, playbackId);
}

export async function playListeningGreeting(sessionId) {
    return playPcm(sessionId, 0, "live-activation", "");
}

async function playPcm(sessionId, sequence, purpose, playbackId) {
    stopResponse(sessionId);
    const record = {
        controller: new AbortController(),
        sources: new Set(),
    };
    activePlayback.set(sessionId, record);

    try {
        let response = await fetch(makeUrl(sessionId, sequence, true, purpose, playbackId), {
            credentials: "same-origin",
            cache: "no-store",
            signal: record.controller.signal,
        });
        // A valid guest cookie can coexist with a signed-in session. The guest
        // route hides sessions it does not own with 404; the staff route still
        // performs its own authentication and ownership checks.
        if (response.status === 401 || response.status === 404) {
            await response.body?.cancel();
            response = await fetch(makeUrl(sessionId, sequence, false, purpose, playbackId), {
                credentials: "same-origin",
                cache: "no-store",
                signal: record.controller.signal,
            });
        }

        if (!response.ok) {
            throw new Error(`Speech request failed (${response.status}).`);
        }
        if (!response.headers.get("content-type")?.toLowerCase().startsWith(audioType)) {
            throw new Error("The Gateway returned an unsupported audio format.");
        }
        if (response.headers.get("x-swiftaid-session-id") !== sessionId
            || response.headers.get("x-swiftaid-response-sequence") !== String(sequence)
            || (purpose === null && playbackId
                && response.headers.get("x-swiftaid-playback-id") !== playbackId)
            || response.headers.get("x-swiftaid-sample-format") !== "s16le"
            || response.headers.get("x-swiftaid-channels") !== "1"
            || (purpose !== null
                && response.headers.get("x-swiftaid-speech-purpose") !== purpose)) {
            throw new Error("The Gateway audio identity or format did not match the response.");
        }

        const sampleRate = Number(response.headers.get("x-swiftaid-sample-rate"));
        if (!Number.isInteger(sampleRate) || sampleRate < 8_000 || sampleRate > 48_000) {
            throw new Error("The Gateway returned an unsupported sample rate.");
        }

        const context = getAudioContext();
        if (context.state !== "running") {
            await context.resume();
        }
        if (record.controller.signal.aborted) {
            return;
        }

        const reader = response.body?.getReader();
        if (!reader) {
            throw new Error("This browser cannot stream Ellai audio.");
        }

        let queuedBytes = new Uint8Array(0);
        let scheduledUntil = context.currentTime;
        let totalBytes = 0;
        const ended = [];
        while (true) {
            const { done, value } = await reader.read();
            if (done) {
                break;
            }
            if (record.controller.signal.aborted) {
                return;
            }

            totalBytes += value.byteLength;
            if (totalBytes > maximumAudioBytes) {
                await reader.cancel();
                throw new Error("The Gateway audio stream exceeded its playback limit.");
            }

            const combined = new Uint8Array(queuedBytes.length + value.length);
            combined.set(queuedBytes);
            combined.set(value, queuedBytes.length);
            const completeBytes = combined.length - (combined.length % 2);
            queuedBytes = combined.slice(completeBytes);
            if (completeBytes === 0) {
                continue;
            }

            const samples = new Float32Array(completeBytes / 2);
            const view = new DataView(combined.buffer, combined.byteOffset, completeBytes);
            for (let index = 0; index < samples.length; index += 1) {
                samples[index] = view.getInt16(index * 2, true) / 32768;
            }

            const buffer = context.createBuffer(1, samples.length, sampleRate);
            buffer.copyToChannel(samples, 0);
            const source = context.createBufferSource();
            source.buffer = buffer;
            source.connect(context.destination);
            const startAt = Math.max(scheduledUntil, context.currentTime + 0.01);
            scheduledUntil = startAt + buffer.duration;
            const completion = new Promise(resolve => {
                source.addEventListener("ended", () => {
                    record.sources.delete(source);
                    resolve();
                }, { once: true });
            });
            record.sources.add(source);
            ended.push(completion);
            source.start(startAt);
        }

        if (queuedBytes.length !== 0) {
            throw new Error("The Gateway returned an incomplete PCM sample.");
        }
        await Promise.all(ended);
    } finally {
        // A failed stream must not leave scheduled sound playing after its handle is lost.
        stopRecord(record);
        if (activePlayback.get(sessionId) === record) {
            activePlayback.delete(sessionId);
        }
    }
}
