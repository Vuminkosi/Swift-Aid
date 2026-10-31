using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Shared Silero ONNX runtime with independent state per audio stream.</summary>
public sealed class SileroOnnxVoiceActivityDetector : IVoiceActivityDetector, IDisposable
{
    private const string ModelFileName = "silero_vad_16k_op15.onnx";
    private const int SampleRate = 16_000;
    private const int FrameSamples = 512;
    private const int ContextSamples = 64;
    private const int RecurrentStateSize = 128;
    private readonly InferenceSession _inference;
    private int _disposed;

    public SileroOnnxVoiceActivityDetector()
    {
        var modelPath = Path.Combine(
            AppContext.BaseDirectory, "Ellai", "Voice", "Models", ModelFileName);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("The pinned Silero VAD model file is missing.", modelPath);
        }

        var options = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            EnableCpuMemArena = true,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };

        _inference = new InferenceSession(modelPath, options);
    }

    public IVoiceActivityDetectionSession CreateSession()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return new SileroVoiceActivityDetectionSession(_inference);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _inference.Dispose();
        }
    }

    private sealed class SileroVoiceActivityDetectionSession(InferenceSession inference)
        : IVoiceActivityDetectionSession
    {
        private readonly object _gate = new();
        private readonly float[] _context = new float[ContextSamples];
        private float[] _recurrentState = new float[2 * RecurrentStateSize];
        private long _lastSequence = -1;
        private bool _disposed;

        public ValueTask<VoiceActivityResult> AnalyzeAsync(
            VoiceActivityFrame frame,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(frame);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                ValidateFrame(frame);
                if (_lastSequence >= 0 && frame.Sequence <= _lastSequence)
                {
                    throw new ArgumentException("VAD frame sequence must increase strictly within a stream.", nameof(frame));
                }

                var discontinuity = _lastSequence >= 0 && frame.Sequence != _lastSequence + 1;
                if (discontinuity)
                {
                    ResetState();
                }

                var modelInput = new float[ContextSamples + FrameSamples];
                _context.CopyTo(modelInput, 0);
                frame.Samples.Span.CopyTo(modelInput.AsSpan(ContextSamples));

                var inputs = new[]
                {
                    NamedOnnxValue.CreateFromTensor(
                        "input", new DenseTensor<float>(modelInput, [1, modelInput.Length])),
                    NamedOnnxValue.CreateFromTensor(
                        "state", new DenseTensor<float>(_recurrentState, [2, 1, RecurrentStateSize])),
                    NamedOnnxValue.CreateFromTensor(
                        "sr", new DenseTensor<long>(new long[] { SampleRate }, new[] { 1 }))
                };

                using var outputs = inference.Run(inputs);
                var probabilityTensor = outputs.FirstOrDefault(output => output.Name == "output")?.AsTensor<float>()
                    ?? throw new InvalidOperationException("Silero VAD did not return its speech probability output.");
                var nextState = outputs.FirstOrDefault(output => output.Name == "stateN")?.AsTensor<float>()
                    ?? throw new InvalidOperationException("Silero VAD did not return its recurrent state output.");
                if (nextState.Length != _recurrentState.Length || probabilityTensor.Length != 1)
                {
                    throw new InvalidOperationException("Silero VAD returned an incompatible tensor shape.");
                }

                var probability = probabilityTensor.ToArray()[0];
                if (!float.IsFinite(probability) || probability is < 0 or > 1)
                {
                    throw new InvalidOperationException("Silero VAD returned an invalid speech probability.");
                }

                nextState.ToArray().CopyTo(_recurrentState, 0);
                frame.Samples.Span[^ContextSamples..].CopyTo(_context);
                _lastSequence = frame.Sequence;

                return ValueTask.FromResult(new VoiceActivityResult(
                    frame.Sequence, probability, probability >= 0.5f, discontinuity));
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ResetState();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                ResetState();
            }
        }

        private static void ValidateFrame(VoiceActivityFrame frame)
        {
            if (frame.SampleRate != SampleRate || frame.Samples.Length != FrameSamples)
            {
                throw new ArgumentException(
                    "Silero 16 kHz VAD requires exactly 512 mono samples per frame.", nameof(frame));
            }

            if (frame.Sequence < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frame), "Frame sequence must not be negative.");
            }

            foreach (var sample in frame.Samples.Span)
            {
                if (!float.IsFinite(sample) || sample is < -1f or > 1f)
                {
                    throw new ArgumentException(
                        "VAD input samples must be finite normalized PCM values between -1 and 1.", nameof(frame));
                }
            }
        }

        private void ResetState()
        {
            Array.Clear(_context);
            Array.Clear(_recurrentState);
            _lastSequence = -1;
        }
    }
}
