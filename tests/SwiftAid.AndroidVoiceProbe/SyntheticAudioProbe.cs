using System.Buffers.Binary;
using Android.App;
using Android.Media;
using Android.OS;
using SwiftAid.Mobile.Services.Audio;

namespace SwiftAid.AndroidVoiceProbe;

[Instrumentation(Name = "online.swiftaid.voiceprobe.Runner", TargetPackage = "online.swiftaid.voiceprobe")]
public sealed class SyntheticAudioProbe : Instrumentation
{
    public SyntheticAudioProbe() { }
    public SyntheticAudioProbe(IntPtr handle, Android.Runtime.JniHandleOwnership transfer) : base(handle, transfer) { }

    public override void OnCreate(Bundle? arguments)
    {
        base.OnCreate(arguments);
        Start();
    }

    public override void OnStart()
    {
        var result = new Bundle();
        try
        {
            var path = Path.Combine(TargetContext!.CacheDir!.AbsolutePath, "synthetic-voice.m4a");
            try
            {
                EncodeSyntheticAudio(path);
                var original = File.ReadAllBytes(path);
                var hash = System.Security.Cryptography.SHA256.HashData(original);
                var wave = AndroidSavedReportVoiceDecoder.DecodeAsync(original, CancellationToken.None).GetAwaiter().GetResult();
                if (wave.Length < 44 + 40000 || !wave.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
                    !wave.AsSpan(8, 4).SequenceEqual("WAVE"u8) ||
                    BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24, 4)) != 24000 ||
                    !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(hash,
                        System.Security.Cryptography.SHA256.HashData(original)))
                    throw new InvalidOperationException("Decoded WAV or original content is unexpected.");
                var audibleSamples = 0;
                for (var i = 44; i + 1 < wave.Length; i += 2)
                    if (Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(i, 2))) > 100) audibleSamples++;
                if (audibleSamples < 1000) throw new InvalidOperationException("Decoded audio is unexpectedly silent.");
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                try
                {
                    AndroidSavedReportVoiceDecoder.DecodeAsync(original, canceled.Token).GetAwaiter().GetResult();
                    throw new InvalidOperationException("Canceled decoding completed.");
                }
                catch (System.OperationCanceledException) { }
                result.PutString("probe", $"PASS synthetic AAC decoded to 24 kHz WAV ({wave.Length} bytes); original preserved; cancellation honored");
                Finish(Result.Ok, result);
            }
            finally { File.Delete(path); }
        }
        catch (Exception exception)
        {
            result.PutString("probe", $"FAIL {exception.GetType().Name}: {exception.Message}");
            Finish(Result.Canceled, result);
        }
    }

    private static void EncodeSyntheticAudio(string path)
    {
        const int sampleRate = 44100;
        using var format = MediaFormat.CreateAudioFormat("audio/mp4a-latm", sampleRate, 1)!;
        format.SetInteger(MediaFormat.KeyBitRate, 64000);
        format.SetInteger("aac-profile", 2);
        using var codec = MediaCodec.CreateEncoderByType("audio/mp4a-latm")!;
        using var muxer = new MediaMuxer(path, MuxerOutputType.Mpeg4);
        using var info = new MediaCodec.BufferInfo();
        codec.Configure(format, null, null, MediaCodecConfigFlags.Encode);
        codec.Start();
        var muxerStarted = false;
        var track = -1;
        var frame = 0;
        var inputDone = false;
        var outputDone = false;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!outputDone)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Synthetic audio encoding timed out.");
            if (!inputDone)
            {
                var index = codec.DequeueInputBuffer(10000);
                if (index >= 0)
                {
                    using var buffer = codec.GetInputBuffer(index)!;
                    buffer.Clear();
                    if (frame >= sampleRate)
                    {
                        codec.QueueInputBuffer(index, 0, 0, 1000000, MediaCodecBufferFlags.EndOfStream);
                        inputDone = true;
                    }
                    else
                    {
                        var count = Math.Min(Math.Min(buffer.Capacity() / 2, 2048), sampleRate - frame);
                        var bytes = new byte[count * 2];
                        for (var i = 0; i < count; i++)
                            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2),
                                (short)(Math.Sin(2 * Math.PI * 440 * (frame + i) / sampleRate) * 12000));
                        buffer.Put(bytes);
                        codec.QueueInputBuffer(index, 0, bytes.Length, (long)frame * 1000000 / sampleRate, MediaCodecBufferFlags.None);
                        frame += count;
                    }
                }
            }
            var output = codec.DequeueOutputBuffer(info, 10000);
            if (output == (int)MediaCodecInfoState.OutputFormatChanged)
            {
                using var outputFormat = codec.OutputFormat;
                track = muxer.AddTrack(outputFormat);
                muxer.Start();
                muxerStarted = true;
            }
            else if (output >= 0)
            {
                try
                {
                    if (info.Size > 0 && (info.Flags & MediaCodecBufferFlags.CodecConfig) == 0)
                    {
                        if (!muxerStarted) throw new InvalidOperationException("Encoder format unavailable.");
                        using var buffer = codec.GetOutputBuffer(output)!;
                        muxer.WriteSampleData(track, buffer, info);
                    }
                    outputDone = (info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
                }
                finally { codec.ReleaseOutputBuffer(output, false); }
            }
        }
        codec.Stop();
        if (muxerStarted) muxer.Stop();
    }
}
