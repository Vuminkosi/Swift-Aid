using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiVoiceTranscriptionServiceTests
{
    [Fact]
    public async Task VoiceConsent_GrantsMicrophoneAndTranscriptionSeparatelyForOneHour()
    {
        var consent = new EllaiConsentService();
        var store = new InMemoryEllaiConsentStore();
        var service = CreateService(consent, store);

        Assert.True(await service.GrantVoiceConsentAsync("session-1", CancellationToken.None));

        var grants = await store.ListBySessionAsync("session-1", CancellationToken.None);
        Assert.Equal(
            new[] { EllaiConsentDimension.Microphone, EllaiConsentDimension.Transcription },
            grants.Select(grant => grant.Dimension).Order().ToArray());
        Assert.All(grants, grant =>
        {
            Assert.Equal(EllaiConsentPurposes.VoiceTranscription, grant.Purpose);
            Assert.Equal(EllaiVoiceTranscriptionService.AudioDataClass, grant.DataClass);
            Assert.InRange(grant.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(59), DateTimeOffset.UtcNow.AddHours(1));
        });
        Assert.True(await service.HasConsentAsync("session-1", CancellationToken.None));
        Assert.False(await service.HasConsentAsync("other-session", CancellationToken.None));
    }

    [Fact]
    public async Task Transcription_DoesNotDispatchWithoutBothConsentGrantsOrConfiguredProvider()
    {
        var consent = new EllaiConsentService();
        var store = new InMemoryEllaiConsentStore();
        var service = CreateService(consent, store);
        await using var audio = new MemoryStream(new byte[256]);

        Assert.Null(await service.TranscribeAsync(
            "session-1", EllaiVoiceCaptureMode.Live, audio, audio.Length, CancellationToken.None));

        Assert.True(await service.GrantVoiceConsentAsync("session-1", CancellationToken.None));
        audio.Position = 0;
        Assert.Null(await service.TranscribeAsync(
            "session-1", EllaiVoiceCaptureMode.Live, audio, audio.Length, CancellationToken.None));
    }

    [Fact]
    public async Task LiveTranscription_FallsBackToConfiguredUploadWhenRealtimeSocketFails()
    {
        var options = new EllaiOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            AsrModel = "muse-voice-transcribe-1.0",
            AsrWebSocketUrl = "wss://127.0.0.1:1/asr/realtime",
            AsrTranscriptionUrl = "https://transcription.test/v1/transcribe",
            AsrMode = "DIARIZATION",
        };
        var handler = new StubAsrHandler();
        var providerServices = new ServiceCollection();
        providerServices.AddSingleton(new MetaAsrRealtimeAdapter(options));
        providerServices.AddSingleton(new MetaAsrUploadAdapter(new HttpClient(handler), options));
        using var provider = providerServices.BuildServiceProvider();
        var consent = new EllaiConsentService();
        var consentStore = new InMemoryEllaiConsentStore();
        var service = new EllaiVoiceTranscriptionService(consent, consentStore, options, provider);
        await service.GrantVoiceConsentAsync("session-fallback", CancellationToken.None);
        await using var audio = new MemoryStream(new byte[4_800]);

        var result = await service.TranscribeAsync(
            "session-fallback", EllaiVoiceCaptureMode.Live, audio, audio.Length, CancellationToken.None);

        Assert.Equal("recognized fallback words", result?.Transcript);
        Assert.True(handler.ReceivedWaveAudio);
    }

    private static EllaiVoiceTranscriptionService CreateService(
        EllaiConsentService consent, InMemoryEllaiConsentStore store)
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        return new EllaiVoiceTranscriptionService(consent, store, new EllaiOptions(), provider);
    }

    private sealed class StubAsrHandler : HttpMessageHandler
    {
        public bool ReceivedWaveAudio { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            ReceivedWaveAudio = Encoding.Latin1.GetString(body).Contains("RIFF", StringComparison.Ordinal)
                && Encoding.Latin1.GetString(body).Contains("ellai-live-fallback.wav", StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"sessionId\":\"asr-session\",\"transcript\":\"recognized fallback words\",\"audioDurationMs\":100,\"turns\":[]}",
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
