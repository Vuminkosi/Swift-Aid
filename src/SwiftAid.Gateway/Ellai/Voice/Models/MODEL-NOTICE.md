# Silero VAD model notice

- Model: `silero_vad_16k_op15.onnx`
- Upstream: [snakers4/silero-vad](https://github.com/snakers4/silero-vad)
- Pinned source revision: `066409f3d9db45a30d21dfcbf9147826ffdd7052`
- Upstream file: `src/silero_vad/data/silero_vad_16k_op15.onnx`
- Model SHA-256: `7ed98ddbad84ccac4cd0aeb3099049280713df825c610a8ed34543318f1b2c49`
- License: MIT; see [SILERO-LICENSE.txt](SILERO-LICENSE.txt).
- Contract: mono float32, 16 kHz, 512 samples per input frame; model input includes 64 prior samples and recurrent state shaped `[2, 1, 128]`.
- Runtime: `Microsoft.ML.OnnxRuntime` CPU package, centrally pinned in `Directory.Packages.props`.

The model is used only to estimate acoustic speech activity. It does not enhance or filter the original audio and it does not determine conversational turn completion.
