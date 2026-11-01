import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { afterEach, test } from "node:test";

const originalFetch = globalThis.fetch;
const originalContext = globalThis.AudioContext;
const originalDocument = globalThis.document;
const source = await readFile(new URL("../../src/SwiftAid.UI/wwwroot/js/ellai-audio-playback.js", import.meta.url), "utf8");
const playback = await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);
let sources = [];
let sampleRates = [];
let failAfterChunk = false;

function configure() {
    sources = [];
    sampleRates = [];
    globalThis.document = { removeEventListener() {} };
    globalThis.AudioContext = class {
        state = "running";
        currentTime = 0;
        destination = {};
        async close() {}
        createBuffer(channels, length, rate) {
            assert.equal(channels, 1);
            sampleRates.push(rate);
            return { duration: length / rate, copyToChannel() {} };
        }
        createBufferSource() {
            const source = {
                stopped: false,
                connect() {},
                addEventListener(_, callback) { this.ended = callback; },
                start() { if (!failAfterChunk) queueMicrotask(() => this.ended()); },
                stop() { this.stopped = true; this.ended(); },
            };
            sources.push(source);
            return source;
        }
    };
    globalThis.fetch = async () => {
        let reads = 0;
        return {
            ok: true,
            headers: new Headers({
                "content-type": "application/vnd.swiftaid.pcm",
                "x-swiftaid-session-id": "session-test",
                "x-swiftaid-response-sequence": "1",
                "x-swiftaid-playback-id": "playback-test",
                "x-swiftaid-sample-format": "s16le",
                "x-swiftaid-channels": "1",
                "x-swiftaid-sample-rate": "16000",
            }),
            body: { getReader: () => ({
                async read() {
                    if (reads++ === 0) return { done: false, value: new Uint8Array([0, 0, 1, 0]) };
                    if (failAfterChunk) throw new Error("Injected stream failure");
                    return { done: true };
                },
            }) },
        };
    };
}

afterEach(async () => {
    await playback.disposeAudioPlayback();
    globalThis.fetch = originalFetch;
    globalThis.AudioContext = originalContext;
    globalThis.document = originalDocument;
});

test("failed PCM reads stop scheduled sound before losing the playback handle", async () => {
    failAfterChunk = true;
    configure();
    await assert.rejects(playback.playResponse("session-test", 1, "playback-test"), /Injected stream failure/);
    assert.equal(sources.length, 1);
    assert.equal(sources[0].stopped, true);
});

test("playback preserves the Gateway sample rate without an extra processing stage", async () => {
    failAfterChunk = false;
    configure();
    await playback.playResponse("session-test", 1, "playback-test");
    assert.deepEqual(sampleRates, [16000]);
    assert.equal(sources.length, 1);
});
