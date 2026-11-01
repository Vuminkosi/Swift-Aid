using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using SwiftAid.Application.Ellai;
using SwiftAid.Gateway.Ellai;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiLanguageClientTests
{
    [EllaiLanguageLiveFact]
    public async Task RealPinnedProviderRespondsThroughGatewayClient()
    {
        using var http = new HttpClient();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EllaiLanguage:Endpoint"] = Environment.GetEnvironmentVariable("SWIFTAID_TEST_LANGUAGE_ENDPOINT"),
            ["EllaiLanguage:InternalKey"] = Environment.GetEnvironmentVariable("SWIFTAID_TEST_LANGUAGE_KEY")
        }).Build();
        var client = new EllaiLanguageClient(http, configuration);
        var translated = await client.TranslateAsync(new("gateway-real-translation", "Good morning.", "eng", "sna"), default);
        Assert.False(string.IsNullOrWhiteSpace(translated.Text));
        Assert.Equal(EllaiLanguageClient.TranslationRevision, translated.ModelRevision);
        var transcription = await client.TranscribeAsync(new("gateway-real-asr", "sna", Convert.ToBase64String(new byte[32000]), 16000), default);
        Assert.Equal(EllaiLanguageClient.TranscriptionRevision, transcription.ModelRevision);
        Assert.Equal("unreviewed", transcription.Quality);
    }

    [Fact]
    public async Task TranslationChecksProviderIdentityAndRetainsUnreviewedStatus()
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new EllaiTranslationResult("turn", "Hello", "sna", "eng",
                EllaiLanguageClient.TranslationModel, EllaiLanguageClient.TranslationRevision, 4, 10, "unreviewed"))
        });
        var result = await Client(handler).TranslateAsync(new("turn", "Mhoro", "sna", "eng"), default);
        Assert.Equal("unreviewed", result.Quality);
        Assert.Equal("Bearer private-test-key", handler.Authorization);
    }

    [Theory]
    [InlineData("stale-turn", "sna", "eng")]
    [InlineData("turn", "nya", "eng")]
    [InlineData("turn", "sna", "tso")]
    public async Task MismatchedProviderResultIsRejected(string request, string source, string target)
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new EllaiTranslationResult(request, "Hello", source, target,
                EllaiLanguageClient.TranslationModel, EllaiLanguageClient.TranslationRevision, 4, 10, "unreviewed"))
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).TranslateAsync(new("turn", "Mhoro", "sna", "eng"), default));
    }

    [Fact]
    public async Task BusyIsRetryableAndUnsupportedLanguageNeverCallsProvider()
    {
        var handler = new Handler(_ => new(HttpStatusCode.TooManyRequests));
        var client = Client(handler);
        var error = await Assert.ThrowsAsync<EllaiLanguageProviderException>(() => client.TranslateAsync(new("turn", "Mhoro", "sna", "eng"), default));
        Assert.True(error.Retryable);
        await Assert.ThrowsAsync<ArgumentException>(() => client.TranslateAsync(new("turn", "text", "nde", "eng"), default));
        Assert.Equal(1, handler.Calls);
    }

    private static EllaiLanguageClient Client(HttpMessageHandler handler) => new(new(handler),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EllaiLanguage:Endpoint"] = "http://language/",
            ["EllaiLanguage:InternalKey"] = "private-test-key"
        }).Build());

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization = request.Headers.Authorization?.ToString();
            Calls++;
            return Task.FromResult(response(request));
        }
    }
}

public sealed class EllaiLanguageLiveFactAttribute : FactAttribute
{
    public EllaiLanguageLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTAID_TEST_LANGUAGE_ENDPOINT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTAID_TEST_LANGUAGE_KEY")))
            Skip = "Requires an explicitly configured local pinned language provider and test credential.";
    }
}
