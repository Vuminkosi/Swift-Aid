export const protocol = "swiftaid.voice.segmented.v1";
const maximumPosition = 24_000 * 60 * 10;

export class SilenceGate {
    constructor(epoch, position = 0, onSpeech = () => {}) {
        if (!Number.isSafeInteger(epoch) || epoch < 1 || !Number.isSafeInteger(position)
            || position < 0 || position > maximumPosition)
            throw new Error("Invalid voice stream identity or position.");
        this.epoch = epoch; this.position = position; this.initialPosition = position;
        this.sequence = 0; this.preRoll = []; this.buffered = 0;
        this.quiet = 0; this.speech = 0; this.speaking = false;
        this.audioBytes = 0; this.onSpeech = onSpeech;
    }
    packet(kind, position, pcm = new Uint8Array()) {
        const bytes = new Uint8Array(32 + pcm.byteLength);
        bytes.set([69, 83, 86, 49]);
        const view = new DataView(bytes.buffer);
        view.setInt32(4, kind, true);
        view.setBigInt64(8, BigInt(++this.sequence), true);
        view.setBigInt64(16, BigInt(position), true);
        view.setBigInt64(24, BigInt(this.epoch), true);
        bytes.set(pcm, 32);
        return bytes;
    }
    audio(position, pcm) { this.audioBytes += pcm.byteLength; return this.packet(1, position, pcm); }
    push(pcm, probability) {
        if (pcm.byteLength === 0 || pcm.byteLength > 8192 || pcm.byteLength % 2 || !Number.isFinite(probability))
            throw new Error("Invalid speech frame.");
        const start = this.position, count = pcm.byteLength / 2, packets = [];
        if (count > maximumPosition || start > maximumPosition - count)
            throw new Error("Voice capture exceeded the segmented-audio position limit.");
        this.position += count;
        const positive = probability >= (this.speaking ? 0.25 : 0.5);
        if (!this.speaking) {
            this.preRoll.push({ position: start, pcm: pcm.slice() }); this.buffered += count;
            while (this.buffered > 19200 && this.preRoll.length > 1) {
                const old = this.preRoll.shift(); this.buffered -= old.pcm.byteLength / 2; old.pcm.fill(0);
            }
            if (!positive) return packets;
            this.speaking = true;
            packets.push(this.packet(2, this.preRoll[0].position));
            for (const frame of this.preRoll) { packets.push(this.audio(frame.position, frame.pcm)); frame.pcm.fill(0); }
            this.preRoll = []; this.buffered = 0;
        } else packets.push(this.audio(start, pcm));
        this.speech = positive ? this.speech + count : 0;
        if (this.speech >= 4608 && this.speech - count < 4608) this.onSpeech();
        this.quiet = positive ? 0 : this.quiet + count;
        if (this.quiet >= 16800) {
            packets.push(this.packet(3, this.position));
            this.speaking = false; this.quiet = this.speech = 0;
        }
        return packets;
    }
    heartbeat() { return this.packet(4, this.position); }
    get suppressedSamples() { return this.position - this.initialPosition - this.audioBytes / 2 - this.buffered; }
    dispose() { for (const frame of this.preRoll) frame.pcm.fill(0); this.preRoll = []; }
}

let loading;
export async function createSilero() {
    const base = new URL("../vendor/vad/", import.meta.url).href;
    async function script(name) {
        await new Promise((resolve, reject) => {
            const element = document.createElement("script");
            element.src = base + name; element.onload = resolve; element.onerror = reject;
            document.head.appendChild(element);
        });
    }
    loading ??= (async () => { await script("ort.wasm.min.js"); await script("vad.bundle.min.js"); })()
        .catch(error => { loading = null; throw error; });
    await loading;
    globalThis.ort.env.wasm.wasmPaths = base;
    globalThis.ort.env.wasm.numThreads = 1;
    globalThis.ort.env.logLevel = "error";
    const detector = await globalThis.ellaiVad.Silero.new(globalThis.ort, async () => {
        const response = await fetch(base + "silero_vad_v5.onnx");
        if (!response.ok) throw new Error("Speech model could not be loaded.");
        return response.arrayBuffer();
    });
    return {
        async analyze(samples) {
            const frame = new Float32Array(512);
            for (let i = 0; i < frame.length; i++) {
                const position = i * 1.5, index = Math.floor(position), fraction = position - index;
                frame[i] = (samples[index] * (1 - fraction) + samples[Math.min(index + 1, 767)] * fraction) / 32768;
            }
            return (await detector.process(frame)).isSpeech;
        },
        destroy: () => detector.release()
    };
}
