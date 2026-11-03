using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Connects framework model calls to the existing session usage ledger.</summary>
public sealed class EllaiFrameworkUsage(string sessionId, EllaiUsageLedger ledger, IEllaiUsageStore store)
{
    private readonly string _sessionId = !string.IsNullOrWhiteSpace(sessionId) ? sessionId : throw new ArgumentException("Session is required.", nameof(sessionId));
    private readonly EllaiUsageLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    private readonly IEllaiUsageStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public string SessionId => _sessionId;

    public bool CanDispatch(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
        => _ledger.TryReserve(_sessionId, Estimate(messages, options));

    public IDisposable? TryBeginModelCall(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
        => _ledger.TryBeginModelCall(_sessionId, Estimate(messages, options));

    private static int Estimate(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        long bytes = Encoding.UTF8.GetByteCount(options?.Instructions ?? string.Empty) + 32;
        foreach (var message in messages)
            bytes += JsonSerializer.SerializeToUtf8Bytes(message).Length + 16;
        if (options?.Tools is not null)
            foreach (var tool in options.Tools.OfType<AIFunctionDeclaration>())
                bytes += Encoding.UTF8.GetByteCount(tool.Name) + Encoding.UTF8.GetByteCount(tool.Description ?? string.Empty)
                    + Encoding.UTF8.GetByteCount(tool.JsonSchema.GetRawText());
        var estimate = (int)Math.Min(int.MaxValue, (bytes + 1) / 2 + (options?.MaxOutputTokens ?? 0));
        return estimate;
    }

    public async Task RecordAsync(ChatResponse response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        var input = (int)Math.Clamp(response.Usage?.InputTokenCount ?? 0, 0, int.MaxValue);
        var output = (int)Math.Clamp(response.Usage?.OutputTokenCount ?? 0, 0, int.MaxValue);
        var sample = new EllaiUsageSample(_sessionId, input, output);
        _ledger.Record(sample);
        await _store.RecordAsync(new StoredUsageSample(_sessionId, input, output,
            _ledger.DollarsFor(sample), DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
    }
}
