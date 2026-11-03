using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiMuseChatClientTests
{
    [Fact]
    public async Task OfficialClientSendsConfiguredRouteAndParsesNativeToolCall()
    {
        var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        using var client = EllaiMuseChatClientFactory.Create(new EllaiOptions
        {
            Enabled = true, ApiUrl = "https://fixture.invalid/v1", ApiKey = "fixture-key", ReasoningModel = "fixture-model"
        }, http);
        var tool = AIFunctionFactory.Create((string value) => value, "lookup");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")],
            new ChatOptions { Tools = [tool], MaxOutputTokens = 123 });

        Assert.Equal("https://fixture.invalid/v1/chat/completions", handler.RequestUri);
        Assert.Equal("Bearer fixture-key", handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal("fixture-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(123, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("lookup", body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        var call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("lookup", call.Name);
    }

    [Fact]
    public void UnconfiguredAndNonMetaRoutesFailClosed()
    {
        Assert.Throws<MetaConfigurationException>(() => EllaiMuseChatClientFactory.Create(new EllaiOptions()));
        Assert.Throws<MetaConfigurationException>(() => EllaiMuseChatClientFactory.Create(new EllaiOptions
        {
            Enabled = true, ApiUrl = "https://fixture.invalid/v1", ApiKey = "fixture-key",
            ReasoningModel = "fixture-model", ReasoningBackend = "ollama"
        }));
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public string RequestUri { get; private set; } = string.Empty;
        public string Authorization { get; private set; } = string.Empty;
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            const string response = """
                {"id":"fixture","object":"chat.completion","created":1,"model":"fixture-model","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{\"value\":\"example\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
