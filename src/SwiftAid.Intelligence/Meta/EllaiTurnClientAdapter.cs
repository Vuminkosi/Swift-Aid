using SwiftAid.Application.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Turn-completion adapter over the routed reasoning lane
/// (Meta/Ollama/serverless via <see cref="EllaiReasoningRouter"/>). Maps the
/// provider result to loop usage; credentials never leave the inner lane.</summary>
public sealed class EllaiProviderTurnClient(EllaiReasoningRouter router) : IEllaiTurnCompletionClient
{
    private readonly EllaiReasoningRouter _router = router ?? throw new ArgumentNullException(nameof(router));

    public async Task<EllaiTurnCompletion> CompleteAsync(
        IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var result = await _router.Resolve().CompleteAsync(messages, maxTokens, ct).ConfigureAwait(false);
        return new EllaiTurnCompletion(
            result.Text, result.Usage.PromptTokens, result.Usage.CompletionTokens);
    }
}
