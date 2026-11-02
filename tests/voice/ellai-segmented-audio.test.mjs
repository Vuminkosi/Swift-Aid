import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
const source = await readFile(new URL("../../src/SwiftAid.UI/wwwroot/js/ellai-segmented-audio.js", import.meta.url), "utf8");
const { SilenceGate } = await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);
const kind = packet => new DataView(packet.buffer).getInt32(4, true);
const position = packet => Number(new DataView(packet.buffer).getBigInt64(16, true));

test("60 seconds idle sends no PCM; speech includes the same 800ms pre-roll as native", () => {
    const gate = new SilenceGate(1);
    for (let i = 0; i < 1875; i++) assert.deepEqual(gate.push(new Uint8Array(1536), 0), []);
    assert.equal(gate.audioBytes, 0);
    assert.equal(position(gate.heartbeat()), 1440000);
    const packets = gate.push(new Uint8Array(1536), 0.9);
    assert.equal(kind(packets[0]), 2);
    assert.equal(position(packets[1]), 1421568);
    assert.equal(gate.audioBytes, 25 * 1536);
    gate.dispose();
});

test("brief pause stays open; 10 second pause emits one end event and suppresses idle PCM", () => {
    const gate = new SilenceGate(1);
    gate.push(new Uint8Array(1536), 0.9);
    for (let i = 0; i < 15; i++) assert.ok(gate.push(new Uint8Array(1536), 0).every(p => kind(p) !== 3));
    gate.push(new Uint8Array(1536), 0.9);
    let ended = 0;
    for (let i = 0; i < 313; i++) ended += gate.push(new Uint8Array(1536), 0).filter(p => kind(p) === 3).length;
    assert.equal(ended, 1);
    const bytes = gate.audioBytes;
    for (let i = 0; i < 100; i++) gate.push(new Uint8Array(1536), 0);
    assert.equal(gate.audioBytes, bytes);
    assert.equal(kind(gate.push(new Uint8Array(1536), 0.9)[0]), 2);
    gate.dispose();
});

test("local interruption waits for confirmed speech, independently of network acknowledgements", () => {
    let interruptions = 0;
    const gate = new SilenceGate(5, 0, () => interruptions++);
    for (let i = 0; i < 5; i++) gate.push(new Uint8Array(1536), 0.9);
    assert.equal(interruptions, 0);
    gate.push(new Uint8Array(1536), 0.9);
    assert.equal(interruptions, 1);
    gate.dispose();
});
