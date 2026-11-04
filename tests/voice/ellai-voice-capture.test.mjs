import assert from "node:assert/strict";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { after, afterEach, test } from "node:test";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

const sourceUrl = new URL("../../src/SwiftAid.UI/wwwroot/js/ellai-voice-capture.js", import.meta.url);
const temporaryModuleDirectory = await mkdtemp(join(tmpdir(), "ellai-voice-capture-"));
const temporaryModulePath = join(temporaryModuleDirectory, "ellai-voice-capture.mjs");
const gateSource = await readFile(new URL("../../src/SwiftAid.UI/wwwroot/js/ellai-segmented-audio.js", import.meta.url), "utf8");
await writeFile(join(temporaryModuleDirectory, "gate.mjs"), gateSource);
const moduleSource = (await readFile(sourceUrl, "utf8"))
    .replace('import { stopResponse } from "./ellai-audio-playback.js";', 'const stopResponse = () => {};')
    .replace('import { SilenceGate, protocol, createSilero } from "./ellai-segmented-audio.js";',
        'import { SilenceGate, protocol } from "./gate.mjs"; const createSilero = async () => ({ analyze: async () => 0.9, destroy: async () => {} });')
    .replaceAll("import.meta.url", JSON.stringify(sourceUrl.href));
await writeFile(temporaryModulePath, moduleSource);
const { startCapture, stopCapture, reconnectVoiceStream } = await import(pathToFileURL(temporaryModulePath).href);

class FakeAudioContext {
    sampleRate = 24000;
    audioWorklet = { addModule: async () => { } };
    destination = {};
    async resume() { }
    async close() { }
    createMediaStreamSource() { return { connect() { }, disconnect() { } }; }
    createGain() { return { gain: {}, connect() { }, disconnect() { } }; }
}

class FakeAudioWorkletNode {
    static last;
    port = {};
    constructor() { FakeAudioWorkletNode.last = this; }
    connect() { }
    disconnect() { }
}

let capturedTracks = [];
let requestedConstraints;
let voiceSockets = [];
const originalSetTimeout = globalThis.setTimeout;
const originalClearTimeout = globalThis.clearTimeout;
const originalWebSocket = globalThis.WebSocket;

class FakeWebSocket {
    static CONNECTING = 0;
    static OPEN = 1;
    static CLOSING = 2;
    static CLOSED = 3;
    url;
    readyState = FakeWebSocket.CONNECTING;
    bufferedAmount = 0;
    sent = [];
    listeners = new Map();
    onopen;
    onclose;
    onerror;

    constructor(url, protocol) {
        this.protocol = protocol;
        this.url = url;
        voiceSockets.push(this);
        queueMicrotask(() => {
            this.readyState = FakeWebSocket.OPEN;
            this.onopen?.();
        });
    }

    send(payload) { this.sent.push(payload); }
    addEventListener(name, callback, options) {
        const listeners = this.listeners.get(name) ?? [];
        listeners.push({ callback, once: options?.once === true });
        this.listeners.set(name, listeners);
    }
    close(code = 1000, reason = "") {
        if (this.readyState === FakeWebSocket.CLOSED) return;
        this.readyState = FakeWebSocket.CLOSED;
        const event = { code, reason, wasClean: true };
        this.onclose?.(event);
        for (const listener of this.listeners.get("close") ?? []) listener.callback(event);
        this.listeners.delete("close");
    }
}

function installBrowser(settings) {
    capturedTracks = [];
    voiceSockets = [];
    Object.defineProperty(globalThis, "navigator", {
        configurable: true,
        value: {
            mediaDevices: {
                getUserMedia: async constraints => {
                    requestedConstraints = constraints;
                    const track = {
                        getSettings: () => settings,
                        stop: () => { track.stopped = true; },
                        stopped: false
                    };
                    capturedTracks.push(track);
                    return { getTracks: () => capturedTracks, getAudioTracks: () => capturedTracks };
                }
            }
        }
    });
    globalThis.AudioContext = FakeAudioContext;
    globalThis.AudioWorkletNode = FakeAudioWorkletNode;
    globalThis.WebSocket = FakeWebSocket;
    globalThis.document = { querySelectorAll: () => [] };
    globalThis.setTimeout = () => 1;
    globalThis.clearTimeout = () => { };
}

afterEach(async () => {
    await stopCapture();
    delete globalThis.AudioContext;
    delete globalThis.AudioWorkletNode;
    delete globalThis.navigator;
    delete globalThis.document;
    if (originalWebSocket === undefined) delete globalThis.WebSocket;
    else globalThis.WebSocket = originalWebSocket;
    globalThis.setTimeout = originalSetTimeout;
    globalThis.clearTimeout = originalClearTimeout;

});

test("live browser capture sends framed PCM without fabricating trailing silence", async () => {
    installBrowser({ echoCancellation: true, noiseSuppression: true, autoGainControl: true });
    const callbacks = [];
    const dotnet = { invokeMethodAsync: async method => callbacks.push(method) };

    await startCapture(dotnet, true, "session-1", "ws://localhost:8080/api/v1/ellai/guest/sessions/session-1/voice/live-stream?voiceCaptureId=capture-1");
    assert.equal(voiceSockets.length, 1);
    assert.equal(voiceSockets[0].url, "ws://localhost:8080/api/v1/ellai/guest/sessions/session-1/voice/live-stream?voiceCaptureId=capture-1");
    const samples = new Float32Array(768);
    samples[0] = -1;
    samples[1] = 1;
    FakeAudioWorkletNode.last.port.onmessage({ data: samples });

    await stopCapture();

    assert.equal(voiceSockets[0].sent.length, 3); // start, real PCM, heartbeat
    assert.deepEqual([...voiceSockets[0].sent[1].slice(32, 36)], [0, 128, 255, 127]);
    assert.equal(voiceSockets[0].sent[1].byteLength, 1568);
    assert.equal(voiceSockets[0].readyState, FakeWebSocket.CLOSED);
    assert.ok(callbacks.includes("OnVoiceWebSocketClosed"));
});

test("capture remains active when browser audio enhancement is unavailable", async () => {
    installBrowser({ echoCancellation: false, autoGainControl: true });
    let reported;
    const dotnet = { invokeMethodAsync: async (method, value) => {
        if (method === "OnVoiceAudioProcessingChanged") reported = value;
    } };

    const status = await startCapture(dotnet);

    assert.match(status, /echo cancellation off/);
    assert.deepEqual(reported, {
        platform: "Web",
        echoCancellationEnabled: false,
        noiseSuppressionEnabled: null,
        automaticGainControlEnabled: true,
        captureActive: true
    });
    assert.equal(capturedTracks[0].stopped, false);
});

test("capture reports effective browser enhancement settings", async () => {
    installBrowser({ echoCancellation: true, noiseSuppression: true, autoGainControl: true });
    let reported;
    const dotnet = { invokeMethodAsync: async (method, value) => {
        if (method === "OnVoiceAudioProcessingChanged") reported = value;
    } };

    const status = await startCapture(dotnet);

    assert.match(status, /echo cancellation on, noise suppression on, automatic gain control on/);
    assert.equal(reported.echoCancellationEnabled, true);
    assert.equal(reported.noiseSuppressionEnabled, true);
    assert.equal(reported.automaticGainControlEnabled, true);
    assert.deepEqual(requestedConstraints.audio.echoCancellation, { ideal: true });
    assert.deepEqual(requestedConstraints.audio.noiseSuppression, { ideal: true });
    assert.deepEqual(requestedConstraints.audio.autoGainControl, { ideal: true });
});

test("socket backpressure preserves capture and replays ordered frames without duplication", async () => {
    installBrowser({ echoCancellation: true, noiseSuppression: true, autoGainControl: true });
    const mirrored = [];
    const callbacks = [];
    const dotnet = { invokeMethodAsync: async (method, samples) => {
        callbacks.push(method);
        if (method === "ReceiveVoicePcmChunk") mirrored.push(...samples);
    } };
    await startCapture(dotnet, true, "session-1", "ws://localhost/voice");
    voiceSockets[0].bufferedAmount = 100000;
    FakeAudioWorkletNode.last.port.onmessage({ data: new Float32Array(768).fill(0.25) });
    FakeAudioWorkletNode.last.port.onmessage({ data: new Float32Array(768).fill(0.5) });
    await new Promise(resolve => originalSetTimeout(resolve, 0));
    assert.equal(capturedTracks[0].stopped, false);
    assert.equal(mirrored.length, 1536);
    assert.equal(callbacks.filter(method => method === "OnVoiceCaptureBackpressure").length, 1);
    const replay = new Uint8Array(1536);
    const view = new DataView(replay.buffer);
    for (let index = 0; index < 768; index++) view.setInt16(index * 2, mirrored[index], true);
    assert.equal(await reconnectVoiceStream("ws://localhost/voice", replay), true);
    const received = new Uint8Array(voiceSockets[1].sent.filter(frame => new DataView(frame.buffer).getInt32(4, true) === 1).flatMap(frame => [...frame.slice(32)]));
    assert.equal(received.length, 3072);
    assert.equal(new DataView(received.buffer).getInt16(0, true), 8192);
    assert.equal(new DataView(received.buffer).getInt16(1536, true), 16384);
    assert.equal(capturedTracks.length, 1);
});

test("browser all-mode echo cancellation is accepted as enabled", async () => {
    installBrowser({ echoCancellation: "all", noiseSuppression: true, autoGainControl: true });
    let reported;
    const dotnet = { invokeMethodAsync: async (method, value) => {
        if (method === "OnVoiceAudioProcessingChanged") reported = value;
    } };

    const status = await startCapture(dotnet);

    assert.match(status, /echo cancellation on/);
    assert.equal(reported.echoCancellationEnabled, true);
});

test("unreported browser echo cancellation remains unknown", async () => {
    installBrowser({ noiseSuppression: true, autoGainControl: true });
    let reported;
    const dotnet = { invokeMethodAsync: async (method, value) => {
        if (method === "OnVoiceAudioProcessingChanged") reported = value;
    } };

    const status = await startCapture(dotnet);

    assert.match(status, /echo cancellation not reported/);
    assert.equal(reported.echoCancellationEnabled, null);
});

after(() => rm(temporaryModuleDirectory, { recursive: true, force: true }));
