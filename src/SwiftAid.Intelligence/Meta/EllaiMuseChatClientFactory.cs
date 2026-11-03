using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Creates the official chat-completions adapter for the configured Meta route.</summary>
public static class EllaiMuseChatClientFactory
{
    public static IChatClient Create(EllaiOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.HasReasoningRoute || options.UsesOllamaBackend || options.UsesServerlessBackend)
            throw new MetaConfigurationException("Ellai Meta reasoning route is not configured.");

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(options.ApiUrl.TrimEnd('/') + "/", UriKind.Absolute)
        };
        if (httpClient is not null)
            clientOptions.Transport = new HttpClientPipelineTransport(httpClient);

        return new ChatClient(options.ReasoningModel, new ApiKeyCredential(options.ApiKey), clientOptions)
            .AsIChatClient();
    }
}
