using System.Text.Json;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T05")]
public sealed class EllaiOptionsTests
{
    [Fact]
    public void BindsBothUnderscoreForms()
    {
        var values = new Dictionary<string, string?>
        {
            ["Ellai_Enabled"] = "true",
            ["Ellai__ApiKey"] = "k",
            ["Ellai__ApiUrl"] = "https://api.meta.ai/v1",
            ["Ellai__ReasoningModel"] = "m",
            ["Ellai__ASR_MODEL"] = "a",
            ["Ellai__ASR_Transcription__URL"] = "https://api.meta.ai/v1/asr/transcribe",
            ["Ellai__ASR_WSS"] = "wss://api.meta.ai/v1/asr/realtime",
        };
        var options = EllaiOptions.Bind(key => values.TryGetValue(key, out var v) ? v : null);

        Assert.True(options.Enabled);
        Assert.True(options.HasReasoningRoute);
        Assert.True(options.HasUploadAsrRoute);
        Assert.True(options.HasRealtimeAsrRoute);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void DisabledOptionsFailClosed()
    {
        var options = EllaiOptions.Bind(_ => null);

        Assert.False(options.HasReasoningRoute);
        Assert.False(options.HasUploadAsrRoute);
        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void RealtimeHandshakeCarriesCredentialInFrame()
    {
        var values = new Dictionary<string, string?>
        {
            ["Ellai_Enabled"] = "true",
            ["Ellai__ApiKey"] = "fake-key",
            ["Ellai__ASR_MODEL"] = "muse-voice-transcribe-1.0",
            ["Ellai__ASR_WSS"] = "wss://api.meta.ai/v1/asr/realtime",
            ["Ellai__ASRMode"] = "DIARIZATION",
        };
        var options = EllaiOptions.Bind(key => values.TryGetValue(key, out var v) ? v : null);
        var handshake = new MetaAsrRealtimeAdapter(options).BuildHandshake();

        Assert.Contains("PCM_24KHZ", handshake, StringComparison.Ordinal);
        Assert.Contains("muse-voice-transcribe-1.0", handshake, StringComparison.Ordinal);
        Assert.Contains("DIARIZATION", handshake, StringComparison.Ordinal);
    }

    [Fact]
    public void ReasonAdapterParsesLiveShapedEnvelope()
    {
        const string json = """{"id":"r1","object":"chat.completion","created":1,"model":"muse-spark-1.3-contributor","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":15,"completion_tokens":285,"total_tokens":300,"completion_tokens_details":{"reasoning_tokens":275}}}""";
        using var doc = JsonDocument.Parse(json);
        var choice = doc.RootElement.GetProperty("choices")[0];

        Assert.Equal("ok", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal(275, doc.RootElement.GetProperty("usage").GetProperty("completion_tokens_details").GetProperty("reasoning_tokens").GetInt32());
    }
}
