using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Per-run tool admission fence. The caller must reject the final agent
/// response when <see cref="FailureCode"/> is set.</summary>
public sealed class EllaiFrameworkToolGuard
{
    private readonly Func<CancellationToken, Task<bool>> _isCurrent;
    private readonly Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> _invoke;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _remaining;
    private string? _failureCode;

    public EllaiFrameworkToolGuard(int maximumCalls, Func<CancellationToken, Task<bool>> isCurrent,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke)
    {
        if (maximumCalls is < 1 or > EllaiCycleRunner.MaxToolCalls)
            throw new ArgumentOutOfRangeException(nameof(maximumCalls));
        _isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
        _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        _remaining = maximumCalls;
    }

    public string? FailureCode => Volatile.Read(ref _failureCode);

    public async Task<EllaiToolResult> InvokeAsync(EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (FailureCode is { } prior)
                return Denied(request.ToolName, prior);
            if (_remaining <= 0 || context.RemainingCalls <= 0)
                return Block(request.ToolName, EllaiToolReasonCodes.BudgetExceeded);
            if (!await _isCurrent(ct).ConfigureAwait(false))
                return Block(request.ToolName, EllaiToolReasonCodes.DeniedByPolicy);

            _remaining = Math.Min(_remaining, context.RemainingCalls);
            var remaining = _remaining--;
            var result = await _invoke(context with { RemainingCalls = remaining }, request, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!await _isCurrent(ct).ConfigureAwait(false))
                return Block(request.ToolName, EllaiToolReasonCodes.DeniedByPolicy);
            if (!result.Allowed && result.ReasonCode != EllaiToolReasonCodes.InvalidArguments)
                Volatile.Write(ref _failureCode, result.ReasonCode);
            return result;
        }
        catch
        {
            Volatile.Write(ref _failureCode, EllaiToolReasonCodes.Unavailable);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private EllaiToolResult Block(string name, string code)
    {
        Volatile.Write(ref _failureCode, code);
        return Denied(name, code);
    }

    private static EllaiToolResult Denied(string name, string code) =>
        new(name, false, "Tool execution is unavailable for this run.", Array.Empty<EvidenceRef>(),
            string.Empty, 0, false, code);
}
