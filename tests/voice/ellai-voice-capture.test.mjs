import assert from "node:assert/strict";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { afterEach, test } from "node:test";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

const sourceUrl = new URL("../../src/SwiftAid.UI/wwwroot/js/ellai-voice-capture.js", import.meta.url);
const temporaryModuleDirectory = await mkdtemp(join(tmpdir(), "ellai-voice-capture-"));
const temporaryModulePath = join(temporaryModuleDirectory, "ellai-voice-capture.mjs");
const moduleSource = (await readFile(sourceUrl, "utf8"))
    .replaceAll("import.meta.url", JSON.stringify(sourceUrl.href));
await writeFile(temporaryModulePath, moduleSource);
const { startCapture, stopCapture } = await import(pathToFileURL(temporaryModulePath).href);

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
    port = {};
    connect() { }
    disconnect() { }
}

let capturedTracks = [];
let requestedConstraints;
const originalSetTimeout = globalThis.setTimeout;
const originalClearTimeout = globalThis.clearTimeout;

function installBrowser(settings) {
    capturedTracks = [];
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
    globalThis.setTimeout = originalSetTimeout;
    globalThis.clearTimeout = originalClearTimeout;
    await rm(temporaryModuleDirectory, { recursive: true, force: true });
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
