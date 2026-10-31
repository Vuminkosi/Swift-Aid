class EllaiVoiceCaptureProcessor extends AudioWorkletProcessor {
    process(inputs, outputs) {
        const input = inputs[0]?.[0];
        const output = outputs[0]?.[0];
        if (output) output.fill(0);
        if (input?.length) {
            const frame = input.slice();
            this.port.postMessage(frame, [frame.buffer]);
        }
        return true;
    }
}

registerProcessor("ellai-voice-capture", EllaiVoiceCaptureProcessor);
