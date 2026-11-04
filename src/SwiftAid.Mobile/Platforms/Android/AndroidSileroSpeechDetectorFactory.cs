using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SwiftAid.UI.Ellai;

namespace SwiftAid.Mobile.Platforms.Android;

/// <summary>The same pinned Silero v5 asset as vad-web. Runs on existing enhanced
/// 24 kHz PCM; never acquires a microphone or adds audio enhancement effects.</summary>
public sealed class AndroidSileroSpeechDetectorFactory : IEllaiClientSpeechDetectorFactory
{
    public event Action<string>? SpeechConfirmed;
    public void NotifySpeechConfirmed(string captureId) => SpeechConfirmed?.Invoke(captureId);
    public IEllaiClientSpeechDetector Create() => new Detector();

    private sealed class Detector : IEllaiClientSpeechDetector
    {
        private readonly InferenceSession _session;
        private readonly float[] _state = new float[256];
        private readonly float[] _input = new float[576];

        public Detector()
        {
            using var asset = typeof(AndroidSileroSpeechDetectorFactory).Assembly
                .GetManifestResourceStream("SwiftAid.Mobile.silero_vad_v5.onnx")
                ?? throw new InvalidOperationException("Client speech model is unavailable.");
            using var bytes = new MemoryStream();
            asset.CopyTo(bytes);
            using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
            _session = new InferenceSession(bytes.ToArray(), options);
        }

        public float AnalyzeFrame(ReadOnlySpan<byte> pcm24Khz)
        {
            if (pcm24Khz.Length != 1536) throw new ArgumentException("Expected 32 ms PCM.");
            // Existing transport is 24 kHz. The model consumes 512 samples at 16 kHz.
            for (var i = 0; i < 512; i++)
            {
                var position = i * 1.5;
                var index = (int)position;
                var first = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm24Khz[(index * 2)..]);
                var second = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm24Khz[(Math.Min(index + 1, 767) * 2)..]);
                _input[64 + i] = (float)((first + (second - first) * (position - index)) / 32768.0);
            }
            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(_input, [1, 576])),
                NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, [2, 1, 128])),
                NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(new long[] { 16000 }, [1]))
            };
            using var outputs = _session.Run(inputs);
            var probability = outputs.First(x => x.Name == "output").AsTensor<float>().ToArray()[0];
            outputs.First(x => x.Name == "stateN").AsTensor<float>().ToArray().CopyTo(_state, 0);
            _input.AsSpan(512, 64).CopyTo(_input);
            return probability;
        }
        public void Dispose() { _session.Dispose(); Array.Clear(_state); Array.Clear(_input); }
    }
}
