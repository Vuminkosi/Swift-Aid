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
        using var timing = EllaiPipelineMetrics.Measure("provider_request");
        var result = await _router.Resolve().CompleteAsync(messages, maxTokens, ct).ConfigureAwait(false);
        return new EllaiTurnCompletion(
            result.Text, result.Usage.PromptTokens, result.Usage.CompletionTokens);
    }

    public async Task<EllaiTurnCompletion> CompleteStreamingAsync(
        IReadOnlyList<EllaiChatMessage> messages,
        int maxTokens,
        Func<string, CancellationToken, ValueTask> onText,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(onText);
        using var timing = EllaiPipelineMetrics.Measure("provider_request");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var first = true;
        var result = await _router.CompleteStreamingAsync(messages, maxTokens, (text, token) =>
            {
                if (first && text.Length > 0)
                {
                    first = false;
                    EllaiPipelineMetrics.Record("provider_first_token", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                return onText(text, token);
            }, ct)
            .ConfigureAwait(false);
        return new EllaiTurnCompletion(
            result.Text, result.Usage.PromptTokens, result.Usage.CompletionTokens);
    }
}
