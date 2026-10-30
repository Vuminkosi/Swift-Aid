using System.Text.Json;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
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

    /// <summary>Read-only documentation search over the approved technical
    /// reference. Never indexes or returns raw repository source.</summary>
    public const string DocsSearch = "ellai_docs_search";

    /// <summary>Read-only retrieval of one approved documentation section
    /// by exact section id, with provenance.</summary>
    public const string DocsGet = "ellai_docs_get";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        State, Evidence, Topic, Capabilities, Receipts, ProductFacts, Now,
        DocsSearch, DocsGet
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
    public const string Unavailable = "Unavailable";
}

/// <summary>Trusted tool-call context; server-constructed, never model-supplied.
/// <see cref="Actor"/> carries the authenticated actor for live domain
/// projections; reads without one stay on static capability content.</summary>
public sealed record EllaiToolContext(
    string SessionId,
    string ScopeEpoch,
    string FeatureId,
    string? ResourceId,
    int RemainingCalls,
    bool ContributorEligible,
    string ConsentVersion = "consent/1",
    string DataClassification = "synthetic",
    ActorContext? Actor = null);

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
    private readonly EllaiTechnicalDocs? _docs;
    private readonly ICaseQueueQuery? _queue;
    private readonly IEllaiApprovalStore? _receipts;

    /// <summary>Consent infrastructure is mandatory: a missing service fails
    /// closed instead of silently bypassing the ReasoningText grant check.
    /// The documentation source is optional: docs tools report an explicit
    /// unavailable outcome while it is unconfigured. The domain queue and
    /// receipt authority are optional: live projections report unavailable
    /// without them, and no Ellai-only unrestricted query is ever introduced.</summary>
    public EllaiToolBroker(
        EllaiPolicyService policy, EllaiConsentService consent,
        EllaiTechnicalDocs? docs = null, ICaseQueueQuery? queue = null,
        IEllaiApprovalStore? receipts = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        _docs = docs;
        _queue = queue;
        _receipts = receipts;
    }

    public async Task<EllaiToolResult> InvokeAsync(
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
            return Bound(Deny(request.ToolName, "Unregistered tool.", EllaiToolReasonCodes.UnregisteredTool));
        }

        if (context.RemainingCalls <= 0)
        {
            return Bound(Deny(request.ToolName, "Model-visible tool budget exhausted.", EllaiToolReasonCodes.BudgetExceeded));
        }

        if (string.IsNullOrWhiteSpace(context.SessionId) || string.IsNullOrWhiteSpace(context.ScopeEpoch))
        {
            return Bound(Deny(request.ToolName, "Missing session scope.", EllaiToolReasonCodes.MissingScope));
        }

        if (request.Arguments.Count > 8)
        {
            return Bound(Deny(request.ToolName, "Too many tool arguments.", EllaiToolReasonCodes.InvalidArguments));
        }

        foreach (var (key, value) in request.Arguments)
        {
            if (value.Length > 120 || LooksLikeCode(value))
            {
                return Bound(Deny(request.ToolName, $"Argument {key} is not an allowed bounded reference.", EllaiToolReasonCodes.InvalidArguments));
            }
        }

        if (!_consent.IsGranted(
                context.SessionId, EllaiConsentDimension.ReasoningText, ToolReadPurpose, DateTimeOffset.UtcNow))
        {
            return Bound(Deny(request.ToolName, "Reasoning-text consent grant is missing or expired.", EllaiToolReasonCodes.ConsentMissing));
        }

        var auth = _policy.Authorize(
            new EllaiAuthorizationRequest(
                context.SessionId, EllaiMode.Ask, context.FeatureId, context.ResourceId,
                null, null, context.ScopeEpoch, context.ConsentVersion, context.ContributorEligible),
            context.DataClassification);

        if (!auth.Permitted)
        {
            return Bound(Deny(request.ToolName, auth.DenialReason ?? "Denied by policy.", EllaiToolReasonCodes.DeniedByPolicy));
        }

        if (string.Equals(canonical, EllaiToolNames.State, StringComparison.Ordinal))
        {
            return Bound(await ReadStateAsync(context, request, ct).ConfigureAwait(false));
        }

        if (string.Equals(canonical, EllaiToolNames.Receipts, StringComparison.Ordinal))
        {
            return Bound(await ReadReceiptsAsync(context, request, ct).ConfigureAwait(false));
        }

        return Bound(canonical switch
        {
            EllaiToolNames.Evidence => Allow(request.ToolName, "Authorized versioned source items.", Math.Min(request.Arguments.Count, MaxItems), request.Arguments.Count > MaxItems),
            EllaiToolNames.Topic => Allow(request.ToolName, "Session topic candidates with checkpoints.", 1, false),
            EllaiToolNames.Capabilities => Allow(request.ToolName, "Current capability descriptors.", 1, false),
            EllaiToolNames.ProductFacts => Allow(request.ToolName, "Versioned nonmedical product facts.", 1, false),
            EllaiToolNames.Now => Allow(request.ToolName, DescribeNow(DateTimeOffset.UtcNow), 1, false),
            EllaiToolNames.DocsSearch => SearchDocs(request),
            EllaiToolNames.DocsGet => GetDocs(request),
            _ => Deny(request.ToolName, "Unregistered tool.", EllaiToolReasonCodes.UnregisteredTool)
        });
    }

    /// <summary>Live <c>ellai_state</c> projections. Without a projection
    /// argument the static permitted-feature description applies. The
    /// <c>queue</c> projection runs the ordinary scoped "my cases" query for
    /// the trusted actor: same handlers, same per-case authorization, no
    /// Ellai-only query path.</summary>
    private async Task<EllaiToolResult> ReadStateAsync(
        EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
    {
        if (!request.Arguments.TryGetValue("projection", out var projection)
            || string.Equals(projection, "feature", StringComparison.OrdinalIgnoreCase))
        {
            return Allow(request.ToolName, "Current permitted feature projection.", 1, false);
        }

        if (!string.Equals(projection, "queue", StringComparison.OrdinalIgnoreCase))
        {
            return Deny(request.ToolName,
                "Unknown state projection. Use projection=queue for the live work queue.",
                EllaiToolReasonCodes.InvalidArguments);
        }

        if (context.Actor is null || _queue is null)
        {
            return Deny(request.ToolName,
                "The live queue projection is not available for this session.",
                EllaiToolReasonCodes.Unavailable);
        }

        var limit = MaxItems;
        if (request.Arguments.TryGetValue("limit", out var rawLimit)
            && (!int.TryParse(rawLimit, out limit) || limit < 1 || limit > MaxItems))
        {
            return Deny(request.ToolName,
                $"limit must be an integer from 1 to {MaxItems}.",
                EllaiToolReasonCodes.InvalidArguments);
        }

        CaseQueueResult result;
        try
        {
            result = await _queue.GetMineAsync(context.Actor, limit, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Deny(request.ToolName,
                "The work queue could not be read right now.",
                EllaiToolReasonCodes.Unavailable);
        }

        if (result.Access == CaseQueueAccess.Unavailable)
        {
            return Deny(request.ToolName,
                "The work queue could not be read right now.",
                EllaiToolReasonCodes.Unavailable);
        }

        if (result.Access == CaseQueueAccess.Denied)
        {
            return Deny(request.ToolName,
                "The work queue is denied for the current actor.",
                EllaiToolReasonCodes.DeniedByPolicy);
        }

        var items = result.Items.Take(limit).ToList();
        var lines = items.Select(item =>
            $"case {item.CaseId:D} · {item.Workflow} · v{item.AggregateVersion} · updated {item.UpdatedAtUtc:O}");
        var evidence = items.Select(item => new EvidenceRef(
            "CaseQueue", item.CaseId.ToString("D"), item.AggregateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), null,
            item.CaseId.ToString("D"))).ToList();
        return new EllaiToolResult(request.ToolName, true, null, evidence,
            items.Count == 0 ? "The permitted work queue is empty." : string.Join("\n", lines),
            items.Count, items.Count >= limit, EllaiToolReasonCodes.Ok);
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

    /// <summary>Live <c>ellai_receipts</c> reads against the durable receipt
    /// authority. A single receipt is looked up by id; otherwise the current
    /// session's receipts list. Session binding is enforced: a receipt from
    /// another session reads as absent, never as a leak. Absence is an
    /// authorized empty, distinct from proof that an effect never happened.</summary>
    private async Task<EllaiToolResult> ReadReceiptsAsync(
        EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
    {
        if (_receipts is null)
        {
            return Deny(request.ToolName,
                "The receipt authority is not available for this session.",
                EllaiToolReasonCodes.Unavailable);
        }

        if (request.Arguments.TryGetValue("receipt", out var receiptId)
            && !string.IsNullOrWhiteSpace(receiptId))
        {
            var receipt = await _receipts.GetAsync(receiptId.Trim(), ct).ConfigureAwait(false);
            if (receipt is null
                || !string.Equals(receipt.SessionId, context.SessionId, StringComparison.Ordinal))
            {
                return Allow(request.ToolName, "No matching receipt in this session.", 0, false);
            }

            var evidence = new[] { new EvidenceRef(
                "Receipt", receipt.ReceiptId, receipt.Status.ToString(), null, receipt.ReceiptId) };
            return new EllaiToolResult(request.ToolName, true, null, evidence,
                DescribeReceipt(receipt), 1, false, EllaiToolReasonCodes.Ok);
        }

        var list = await _receipts.ListBySessionAsync(context.SessionId, ct).ConfigureAwait(false);
        var items = list.Take(MaxItems).ToList();
        var lines = items.Select(DescribeReceipt);
        var refs = items.Select(receipt => new EvidenceRef(
            "Receipt", receipt.ReceiptId, receipt.Status.ToString(), null, receipt.ReceiptId)).ToList();
        return new EllaiToolResult(request.ToolName, true, null, refs,
            items.Count == 0 ? "No receipts recorded in this session." : string.Join("\n", lines),
            items.Count, list.Count > MaxItems, EllaiToolReasonCodes.Ok);
    }

    private static string DescribeReceipt(EllaiReceipt receipt)
    {
        var verified = receipt.VerifiedAtUtc?.ToString("O") ?? "not yet verified";
        var binding = receipt.ApprovedRecipient is null
            ? string.Empty
            : $" · recipient {receipt.ApprovedRecipient}";
        var due = receipt.ApprovedDueAtUtc?.ToString("O") ?? null;
        return $"receipt {receipt.ReceiptId} · action {receipt.ActionId} · status {receipt.Status}" +
            $" · accepted {receipt.CreatedAtUtc:O} · expires {receipt.ApprovalExpiresAtUtc:O}" +
            $" · verified {verified}{binding}" +
            (due is null ? string.Empty : $" · due {due}");
    }

    private EllaiToolResult SearchDocs(EllaiToolRequest request)
    {
        if (_docs is null || !_docs.IsAvailable)
        {
            return Deny(request.ToolName,
                "The documentation source is not configured; no documentation was searched.",
                EllaiToolReasonCodes.Unavailable);
        }

        if (!request.Arguments.TryGetValue("q", out var query) || string.IsNullOrWhiteSpace(query))
        {
            return Deny(request.ToolName,
                "A non-empty q argument is required.",
                EllaiToolReasonCodes.InvalidArguments);
        }

        var limit = EllaiTechnicalDocs.MaxHits;
        if (request.Arguments.TryGetValue("limit", out var rawLimit)
            && (!int.TryParse(rawLimit, out limit) || limit < 1 || limit > EllaiTechnicalDocs.MaxHits))
        {
            return Deny(request.ToolName,
                $"limit must be an integer from 1 to {EllaiTechnicalDocs.MaxHits}.",
                EllaiToolReasonCodes.InvalidArguments);
        }

        var hits = _docs.Search(query, limit);
        if (hits.Count == 0)
        {
            return Deny(request.ToolName,
                "No approved documentation section matches the query.",
                EllaiToolReasonCodes.InvalidArguments);
        }

        var lines = hits.Select(h =>
            $"[{h.SectionId}] {h.Title} (status {h.Status}, rev {h.Revision}): {h.Snippet}");
        return Allow(request.ToolName, string.Join("\n", lines), hits.Count, false);
    }

    private EllaiToolResult GetDocs(EllaiToolRequest request)
    {
        if (_docs is null || !_docs.IsAvailable)
        {
            return Deny(request.ToolName,
                "The documentation source is not configured; no section was retrieved.",
                EllaiToolReasonCodes.Unavailable);
        }

        if (!request.Arguments.TryGetValue("section", out var sectionId) || string.IsNullOrWhiteSpace(sectionId))
        {
            return Deny(request.ToolName,
                "An exact section argument is required.",
                EllaiToolReasonCodes.InvalidArguments);
        }

        var section = _docs.Get(sectionId);
        if (section is null)
        {
            return Deny(request.ToolName,
                $"Unknown documentation section '{sectionId.Trim()}'.",
                EllaiToolReasonCodes.InvalidArguments);
        }

        var summary = $"{section.Title} [{section.SectionId}] " +
            $"(technical.md, status {section.Status}, rev {section.Revision}):\n{section.Body}";
        return Allow(request.ToolName, summary, 1, false);
    }

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
