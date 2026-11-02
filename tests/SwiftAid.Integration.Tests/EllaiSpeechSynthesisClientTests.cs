using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Gateway.Ellai;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiSpeechSynthesisClientTests
{
    [Theory]
    [InlineData("sna")]
    [InlineData("nya")]
    [InlineData("tso")]
    public async Task PublicationPreparesNativeAudioWithItsOriginalLanguage(string language)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        using var prepared = new EllaiPreparedResponseAudio(Create(http), NullLogger<EllaiPreparedResponseAudio>.Instance);
        await prepared.StartAsync(CancellationToken.None);
        try
        {
            prepared.Published(new EllaiSessionResponseDto(1, "session", "Synthetic sentence.", DateTimeOffset.UtcNow,
                "turn", Language: language));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var audio = await prepared.OpenAsync("session", 1, timeout.Token);
            Assert.Equal(new byte[] { 0, 0 }, await audio.Content.ReadAsByteArrayAsync(timeout.Token));
            using var request = JsonDocument.Parse(handler.Body!);
            Assert.Equal(language, request.RootElement.GetProperty("language").GetString());
        }
        finally { await prepared.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("eng", "af_heart")]
    [InlineData("eng", null)]
    [InlineData("sna", "default")]
    [InlineData("nya", "default")]
    [InlineData("tso", "default")]
    public async Task SendsExactLanguageAndVoiceWithoutEnglishFallback(string language, string? voice)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var client = Create(http);
        using var response = await client.SynthesizeAsync("test", "Synthetic sentence.", language, voice, CancellationToken.None);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(language, body.RootElement.GetProperty("language").GetString());
        Assert.Equal(voice ?? "default", body.RootElement.GetProperty("voiceId").GetString());
        Assert.Equal("Bearer test-key", handler.Authorization);
    }

    [Theory]
    [InlineData("ssw", "default")]
    [InlineData("sna", "af_heart")]
    [InlineData("eng", "uninstalled")]
    public async Task RejectsUnsupportedSelectionsBeforeNetworkCall(string language, string voice)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Create(http).SynthesizeAsync(
            "test", "Synthetic sentence.", language, voice, CancellationToken.None));
        Assert.Null(handler.Body);
    }

    private static EllaiSpeechSynthesisClient Create(HttpClient http) => new(http,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EllaiTts:Endpoint"] = "http://speech.test/",
            ["EllaiTts:InternalKey"] = "test-key",
        }).Build());

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 0]) };
            response.Content.Headers.ContentType = new("application/vnd.swiftaid.pcm");
            response.Headers.Add("X-SwiftAid-Voice-Id", "test-voice");
            response.Headers.Add("X-SwiftAid-Sample-Rate", "16000");
            response.Headers.Add("X-SwiftAid-Channels", "1");
            response.Headers.Add("X-SwiftAid-Sample-Format", "s16le");
            return response;
        }
    }
}
