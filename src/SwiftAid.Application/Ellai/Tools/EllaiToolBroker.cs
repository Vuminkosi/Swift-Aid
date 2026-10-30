using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Brokered read-tool names exposed to the model. The model chooses
/// reads or emits a decision; it never receives execute/approve/sql/shell.</summary>
public static class EllaiToolNames
{
    public const string State = "ellai_state";
    public const string Evidence = "ellai_evidence";
    public const string Topic = "ellai_topic";
    public const string Capabilities = "ellai_capabilities";
    public const string Receipts = "ellai_receipts";
    public const string ProductFacts = "ellai_productfacts";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        State, Evidence, Topic, Capabilities, Receipts, ProductFacts
    };
}

/// <summary>Trusted tool-call context; server-constructed, never model-supplied.</summary>
public sealed record EllaiToolContext(
    string SessionId,
    string ScopeEpoch,
    string FeatureId,
    string? ResourceId,
    int RemainingCalls,
    bool ContributorEligible);

/// <summary>Tool request with bounded arguments.</summary>
public sealed record EllaiToolRequest(
    string ToolName,
    IReadOnlyDictionary<string, string> Arguments);

/// <summary>Minimized typed tool result with version stamps.</summary>
public sealed record EllaiToolResult(
    string ToolName,
    bool Allowed,
    string? DenialReason,
    IReadOnlyList<EvidenceRef> Evidence,
    string ResultSummary,
    int ItemCount,
    bool Truncated);

/// <summary>Authorized typed evidence/app-state tool broker (ELL-T07 groundwork).
/// Every invocation rechecks scope; unregistered tools, arbitrary queries and
/// over-broad reads are rejected before execution.</summary>
public sealed class EllaiToolBroker(EllaiPolicyService policy)
{
    public const int MaxItems = 20;

    public Task<EllaiToolResult> InvokeAsync(
        EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (!EllaiToolNames.All.Contains(request.ToolName))
        {
            return Task.FromResult(Deny(request.ToolName, "Unregistered tool."));
        }

        if (context.RemainingCalls <= 0)
        {
            return Task.FromResult(Deny(request.ToolName, "Model-visible tool budget exhausted."));
        }

        if (string.IsNullOrWhiteSpace(context.SessionId) || string.IsNullOrWhiteSpace(context.ScopeEpoch))
        {
            return Task.FromResult(Deny(request.ToolName, "Missing session scope."));
        }

        if (request.Arguments.Count > 8)
        {
            return Task.FromResult(Deny(request.ToolName, "Too many tool arguments."));
        }

        foreach (var (key, value) in request.Arguments)
        {
            if (value.Length > 120 || LooksLikeCode(value))
            {
                return Task.FromResult(Deny(request.ToolName, $"Argument {key} is not an allowed bounded reference."));
            }
        }

        var auth = policy.Authorize(
            new EllaiAuthorizationRequest(
                context.SessionId, EllaiMode.Ask, context.FeatureId, context.ResourceId,
                null, null, context.ScopeEpoch, "consent/1", context.ContributorEligible),
            "synthetic");

        if (!auth.Permitted)
        {
            return Task.FromResult(Deny(request.ToolName, auth.DenialReason ?? "Denied by policy."));
        }

        return Task.FromResult(request.ToolName switch
        {
            EllaiToolNames.State => Allow(request.ToolName, "Current permitted feature projection.", 1, false),
            EllaiToolNames.Evidence => Allow(request.ToolName, "Authorized versioned source items.", Math.Min(request.Arguments.Count, MaxItems), request.Arguments.Count > MaxItems),
            EllaiToolNames.Topic => Allow(request.ToolName, "Session topic candidates with checkpoints.", 1, false),
            EllaiToolNames.Capabilities => Allow(request.ToolName, "Current capability descriptors.", 1, false),
            EllaiToolNames.Receipts => Allow(request.ToolName, "Existing receipts with linked state.", 1, false),
            EllaiToolNames.ProductFacts => Allow(request.ToolName, "Versioned nonmedical product facts.", 1, false),
            _ => Deny(request.ToolName, "Unregistered tool.")
        });
    }

    private static EllaiToolResult Allow(string tool, string summary, int count, bool truncated) =>
        new(tool, true, null, Array.Empty<EvidenceRef>(), summary, count, truncated);

    private static EllaiToolResult Deny(string tool, string reason) =>
        new(tool, false, reason, Array.Empty<EvidenceRef>(), string.Empty, 0, false);

    private static bool LooksLikeCode(string value) =>
        value.Contains('<') || value.Contains('>') || value.Contains("SELECT ", StringComparison.OrdinalIgnoreCase)
        || value.Contains("http://", StringComparison.OrdinalIgnoreCase)
        || value.Contains("https://", StringComparison.OrdinalIgnoreCase);
}
