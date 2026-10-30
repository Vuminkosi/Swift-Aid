using System.Text.Json;
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

    /// <summary>Deterministic server-clock read: current UTC instant in
    /// round-trip form plus Unix milliseconds, so the model can anchor
    /// present vs past without guessing. Carries no domain data and follows
    /// the same consent, budget and byte-bound pipeline as every read.</summary>
    public const string Now = "ellai_now";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        State, Evidence, Topic, Capabilities, Receipts, ProductFacts, Now
    };
}

/// <summary>Machine-readable tool-result reason codes. Denials keep the
/// human-readable <see cref="EllaiToolResult.DenialReason"/> alongside the
/// code so deterministic clients can branch without parsing prose.</summary>
public static class EllaiToolReasonCodes
{
    public const string Ok = "Ok";
    public const string UnregisteredTool = "UnregisteredTool";
    public const string BudgetExceeded = "BudgetExceeded";
    public const string MissingScope = "MissingScope";
    public const string InvalidArguments = "InvalidArguments";
    public const string ConsentMissing = "ConsentMissing";
    public const string DeniedByPolicy = "DeniedByPolicy";
    public const string ResultTooLarge = "ResultTooLarge";
}

/// <summary>Trusted tool-call context; server-constructed, never model-supplied.</summary>
public sealed record EllaiToolContext(
    string SessionId,
    string ScopeEpoch,
    string FeatureId,
    string? ResourceId,
    int RemainingCalls,
    bool ContributorEligible,
    string ConsentVersion = "consent/1",
    string DataClassification = "synthetic");

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
    bool Truncated,
    string ReasonCode = EllaiToolReasonCodes.Ok);

/// <summary>Authorized typed evidence/app-state tool broker (ELL-T07 groundwork).
/// Every invocation rechecks scope; unregistered tools, arbitrary queries and
/// over-broad reads are rejected before execution.</summary>
public sealed class EllaiToolBroker
{
    public const int MaxItems = 20;

    /// <summary>Maximum model-visible result envelope in serialized UTF-8
    /// bytes, including source metadata and errors. Multibyte text and JSON
    /// escaping count as sent, not as .NET chars.</summary>
    public const int MaxResultBytes = 32768;

    /// <summary>Purpose bound to every brokered model-visible read.</summary>
    public const string ToolReadPurpose = "ellai-tool-read";

    private static readonly JsonSerializerOptions EnvelopeJson = new(JsonSerializerDefaults.Web);

    /// <summary>Compatibility aliases for previously reported tool names.
    /// Each delegates to the same canonical handler, authorization,
    /// accounting and byte bounds. Aliases are accepted but never
    /// advertised: <see cref="EllaiToolNames.All"/> stays canonical.</summary>
    private static readonly IReadOnlyDictionary<string, string> AliasToCanonical =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ellai_voice_capability"] = EllaiToolNames.Capabilities,
            ["ellai_guidance_session"] = EllaiToolNames.State,
        };

    private readonly EllaiPolicyService _policy;
    private readonly EllaiConsentService _consent;

    /// <summary>Consent infrastructure is mandatory: a missing service fails
    /// closed instead of silently bypassing the ReasoningText grant check.</summary>
    public EllaiToolBroker(EllaiPolicyService policy, EllaiConsentService consent)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
    }

    public Task<EllaiToolResult> InvokeAsync(
        EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var canonical = AliasToCanonical.TryGetValue(request.ToolName, out var target)
            ? target
            : request.ToolName;

        if (!EllaiToolNames.All.Contains(canonical))
        {
            return Task.FromResult(Bound(Deny(request.ToolName, "Unregistered tool.", EllaiToolReasonCodes.UnregisteredTool)));
        }

        if (context.RemainingCalls <= 0)
        {
            return Task.FromResult(Bound(Deny(request.ToolName, "Model-visible tool budget exhausted.", EllaiToolReasonCodes.BudgetExceeded)));
        }

        if (string.IsNullOrWhiteSpace(context.SessionId) || string.IsNullOrWhiteSpace(context.ScopeEpoch))
        {
            return Task.FromResult(Bound(Deny(request.ToolName, "Missing session scope.", EllaiToolReasonCodes.MissingScope)));
        }

        if (request.Arguments.Count > 8)
        {
            return Task.FromResult(Bound(Deny(request.ToolName, "Too many tool arguments.", EllaiToolReasonCodes.InvalidArguments)));
        }

        foreach (var (key, value) in request.Arguments)
        {
            if (value.Length > 120 || LooksLikeCode(value))
            {
                return Task.FromResult(Bound(Deny(request.ToolName, $"Argument {key} is not an allowed bounded reference.", EllaiToolReasonCodes.InvalidArguments)));
            }
        }

        if (!_consent.IsGranted(
                context.SessionId, EllaiConsentDimension.ReasoningText, ToolReadPurpose, DateTimeOffset.UtcNow))
        {
            return Task.FromResult(Bound(Deny(request.ToolName, "Reasoning-text consent grant is missing or expired.", EllaiToolReasonCodes.ConsentMissing)));
        }

        var auth = _policy.Authorize(
            new EllaiAuthorizationRequest(
                context.SessionId, EllaiMode.Ask, context.FeatureId, context.ResourceId,
                null, null, context.ScopeEpoch, context.ConsentVersion, context.ContributorEligible),
            context.DataClassification);

        if (!auth.Permitted)
        {
            return Task.FromResult(Bound(Deny(request.ToolName, auth.DenialReason ?? "Denied by policy.", EllaiToolReasonCodes.DeniedByPolicy)));
        }

        return Task.FromResult(Bound(canonical switch
        {
            EllaiToolNames.State => Allow(request.ToolName, "Current permitted feature projection.", 1, false),
            EllaiToolNames.Evidence => Allow(request.ToolName, "Authorized versioned source items.", Math.Min(request.Arguments.Count, MaxItems), request.Arguments.Count > MaxItems),
            EllaiToolNames.Topic => Allow(request.ToolName, "Session topic candidates with checkpoints.", 1, false),
            EllaiToolNames.Capabilities => Allow(request.ToolName, "Current capability descriptors.", 1, false),
            EllaiToolNames.Receipts => Allow(request.ToolName, "Existing receipts with linked state.", 1, false),
            EllaiToolNames.ProductFacts => Allow(request.ToolName, "Versioned nonmedical product facts.", 1, false),
            EllaiToolNames.Now => Allow(request.ToolName, DescribeNow(DateTimeOffset.UtcNow), 1, false),
            _ => Deny(request.ToolName, "Unregistered tool.", EllaiToolReasonCodes.UnregisteredTool)
        }));
    }

    /// <summary>Measures the model-visible envelope exactly as serialized for
    /// the wire (ASP.NET Web JSON defaults), in UTF-8 bytes.</summary>
    public static int MeasureEnvelopeBytes(EllaiToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.SerializeToUtf8Bytes(result, EnvelopeJson).Length;
    }

    /// <summary>Enforces the aggregate serialized-byte bound on any result,
    /// including errors. An oversized result becomes an explicit
    /// <c>ResultTooLarge</c> denial; JSON is never sliced or truncated into
    /// a misleading claim.</summary>
    public static EllaiToolResult ApplyBounds(EllaiToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (MeasureEnvelopeBytes(result) <= MaxResultBytes)
        {
            return result;
        }

        return new EllaiToolResult(
            result.ToolName, false,
            "Tool result exceeded the 32,768-byte model-visible bound; retry with a smaller projection.",
            Array.Empty<EvidenceRef>(), string.Empty, 0, false,
            EllaiToolReasonCodes.ResultTooLarge);
    }

    private static EllaiToolResult Bound(EllaiToolResult result) => ApplyBounds(result);

    private static EllaiToolResult Allow(string tool, string summary, int count, bool truncated) =>
        new(tool, true, null, Array.Empty<EvidenceRef>(), summary, count, truncated, EllaiToolReasonCodes.Ok);

    internal static string DescribeNow(DateTimeOffset now) =>
        $"Current server time (UTC): {now:O} (unix {now.ToUnixTimeMilliseconds()} ms, {now.DayOfWeek}). " +
        "Earlier instants are the past; later instants are the future. Human-confirmed local times " +
        "normalize to this clock for approvals and due times.";

    private static EllaiToolResult Deny(string tool, string reason, string reasonCode) =>
        new(tool, false, reason, Array.Empty<EvidenceRef>(), string.Empty, 0, false, reasonCode);

    private static bool LooksLikeCode(string value) =>
        value.Contains('<') || value.Contains('>') || value.Contains("SELECT ", StringComparison.OrdinalIgnoreCase)
        || value.Contains("http://", StringComparison.OrdinalIgnoreCase)
        || value.Contains("https://", StringComparison.OrdinalIgnoreCase);
}
