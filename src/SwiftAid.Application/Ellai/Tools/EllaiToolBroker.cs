using System.Text.Json;
using System.Globalization;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Correlation;
using SwiftAid.Application.Cases.Spatial;
using SwiftAid.Application.HealthEvidence;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Application.Intake;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Contracts.Sessions;

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

    /// <summary>Permission-filtered semantic catalogue slice: registered
    /// control descriptors, objectives and proof references for one feature.</summary>
    public const string UiCatalogue = "ellai_ui_catalogue";

    /// <summary>Live UI observation through the client-posted mounted-control
    /// channel: Ready only for entitled controls the current view reports
    /// mounted. Marked ClientObservation with server-stamped freshness.</summary>
    public const string UiObserve = "ellai_ui_observe";
    public const string IngestionSchema = "ellai_ingestion_schema";
    public const string ValidateObservations = "ellai_validate_observations";
    public const string Locations = "ellai_locations";
    public const string ImportDestinations = "ellai_import_destinations";
    public const string Imports = "ellai_imports";
    public const string ImportStatus = "ellai_import_status";
    public const string ClassificationEvidence = "ellai_classification_evidence";
    public const string ClassificationSnapshot = "ellai_classification_snapshot";
    public const string SurveillanceSignals = "ellai_surveillance_signals";
    public const string OwnedCaseCount = "ellai_owned_case_count";
    public const string OwnedRegionCounts = "ellai_owned_region_counts";
    public const string ApprovedHealthSources = "ellai_approved_health_sources";
    public const string CaseTimeline = "ellai_case_timeline";
    public const string PublicSessions = "ellai_public_sessions";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        State, Evidence, Topic, Capabilities, Receipts, ProductFacts, Now,
        DocsSearch, DocsGet, UiCatalogue, UiObserve, IngestionSchema, ValidateObservations, Locations,
        ImportDestinations, Imports, ImportStatus, ClassificationEvidence, ClassificationSnapshot, SurveillanceSignals, OwnedCaseCount, OwnedRegionCounts, ApprovedHealthSources, CaseTimeline, PublicSessions
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
    string DataClassification = EllaiProcessingClassification.UnknownOrigin,
    ActorContext? Actor = null,
    IReadOnlySet<string>? AllowedTools = null,
    string ConsentPurpose = EllaiToolBroker.ToolReadPurpose)
{
    /// <summary>Server-owned absolute deadline for the current tool invocation.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset? ToolDeadlineUtc { get; init; }
}

/// <summary>Tool request with bounded arguments. <see cref="CallId"/> is the
/// model-assigned call correlation id: it travels untouched through
/// validation and execution so tool results re-enter the next model turn
/// under the same id. It never affects authorization.</summary>
public sealed record EllaiToolRequest(
    string ToolName,
    IReadOnlyDictionary<string, string> Arguments,
    string? CallId = null);

/// <summary>Minimized typed tool result with version stamps.</summary>
public sealed record EllaiToolResult(
    string ToolName,
    bool Allowed,
    string? DenialReason,
    IReadOnlyList<EvidenceRef> Evidence,
    string ResultSummary,
    int ItemCount,
    bool Truncated,
    string ReasonCode = EllaiToolReasonCodes.Ok)
{
    /// <summary>Internal replay limit; never part of the model-visible tool result.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset? HistoryValidUntilUtc { get; init; }

    /// <summary>Internal version of a non-sensitive replayable result shape.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? HistoryReplayVersion { get; init; }

    /// <summary>Internal, bounded provenance for rechecking an aggregate result.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public EllaiCountReplayScope? CountReplayScope { get; init; }

    /// <summary>Internal query identity for a replayable catalogue read.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public EllaiUiCatalogueReplayScope? UiCatalogueReplayScope { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public EllaiCaseTimelineReplayScope? CaseTimelineReplayScope { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public EllaiUiObservationReplayScope? UiObservationReplayScope { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public EllaiLocationReplayScope? LocationReplayScope { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public EllaiLocationReplayScope? HealthSourceReplayScope { get; init; }
}

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

    /// <summary>Resolves a model-supplied tool name to its canonical handler:
    /// canonical names resolve to themselves, compatibility aliases to their
    /// canonical target, anything else fails. Single source for the broker
    /// dispatch and the model-turn parser so both agree.</summary>
    public static bool TryResolveCanonical(string name, out string canonical)
    {
        if (EllaiToolNames.All.Contains(name))
        {
            canonical = name;
            return true;
        }

        if (AliasToCanonical.TryGetValue(name, out var target))
        {
            canonical = target;
            return true;
        }

        canonical = string.Empty;
        return false;
    }

    /// <summary>Deterministic tool catalogue summary for the reasoning
    /// system prompt: canonical names with argument hints. The model still
    /// passes scope, authorization, budget and byte-bound checks per call;
    /// consent applies only to explicitly consent-scoped processing lanes.</summary>
    private static readonly string[] ToolDescriptions =
    {
        $"{EllaiToolNames.State}(projection=[feature|queue], limit=1..20, country?, workflow?, sort?) — permitted feature or owned-case queue. country is an uppercase two-letter code (Zimbabwe=ZW, Eswatini=SZ); workflow is Reported, UnderReview, Active, AwaitingFollowUp or Resolved; sort defaults to latest-reported and can be latest-updated. Reported is a recorded workflow, not proof that no human reviewed a case. Classification review status is not supplied; clarify ambiguous unreviewed requests.",
        $"{EllaiToolNames.Evidence}(ref=case-results, field=[country|workflow]?) — re-read retained case queue references under current access.",
        $"{EllaiToolNames.Topic}(ref) — session topic candidates with checkpoints. Use ref=case-results to recall the last retained case list with fresh authorization; never invent a remembered result set.",
        $"{EllaiToolNames.Capabilities}(feature?) — capability readiness, no credentials.",
        $"{EllaiToolNames.Receipts}(receipt?) — durable receipts, session-bound.",
        $"{EllaiToolNames.ProductFacts}(topic?) — versioned nonmedical product facts.",
        $"{EllaiToolNames.Now}() — current server UTC time.",
        $"{EllaiToolNames.DocsSearch}(q, limit?) — approved technical reference search.",
        $"{EllaiToolNames.DocsGet}(section) — one approved section excerpt.",
        $"{EllaiToolNames.UiCatalogue}(feature?, goal?, limit?) — entitled control descriptors.",
        $"{EllaiToolNames.UiObserve}(controls, view=current, context?, feature?) — live mounted-control facts.",
        $"{EllaiToolNames.IngestionSchema}() — canonical case CSV fields, capture requirements and authority boundaries; no submission.",
        $"{EllaiToolNames.ValidateObservations}(observations_json) — validate a bounded JSON array of original reported strings; no inference or submission.",
        $"{EllaiToolNames.SurveillanceSignals}(case_id) - read authorized persisted review signals linked to one case; not an outbreak declaration.",
        $"{EllaiToolNames.ClassificationSnapshot}(case_id) - read authorized persisted classification records for one case; never a diagnosis.",
        $"{EllaiToolNames.PublicSessions}(status?) - list published public sessions, optionally filtered by Scheduled, Completed, or Cancelled; no registration or scheduling.",
        $"{EllaiToolNames.Locations}(q, country?, limit?) — search the public geographic registry; present matches for user selection, never guess a location.",
        $"{EllaiToolNames.ImportDestinations}() — current authorized organization/workspace/country destinations for Workspace imports; staff only.",
        $"{EllaiToolNames.Imports}(workspace_id, organization_id, country) — the current user's recent imports in one permitted destination; staff only.",
        $"{EllaiToolNames.ImportStatus}(import_id) — the current user's durable import state and saved/rejected/retry counts; no case contents and no submission.",
        $"{EllaiToolNames.ClassificationEvidence}(case_id) — persisted semantic classification evidence for an authorized current case revision; no inference, diagnosis or clinical probability.",
        $"{EllaiToolNames.OwnedCaseCount}(window_days) — count current-actor owned, readable managed cases created within 1..365 days; rejects incomplete results. This is a case count, not a diagnosis, incidence or animal count.",
        $"{EllaiToolNames.OwnedRegionCounts}() — rank unsuppressed administrative-region counts for the current officer's readable, owned cases created in the last 30 days. Not population incidence or a hotspot map.",
        $"{EllaiToolNames.ApprovedHealthSources}(topic, jurisdiction, language) — list explicitly approved, current source references only; no medical advice or generated health education.",
        $"{EllaiToolNames.CaseTimeline}(case_id, include_related?) — read authorized case history and, when requested, possible related cases; older entries may exist.",
    };

    /// <summary>Returns the existing model-facing description for a canonical tool.</summary>
    public static string DescribeTool(string toolName) =>
        ToolDescriptions.FirstOrDefault(description =>
            description.StartsWith(toolName + "(", StringComparison.Ordinal))
        ?? throw new ArgumentException("Tool has no registered description.", nameof(toolName));

    public static string DescribeCatalogue(IReadOnlySet<string>? allowedTools = null) =>
        string.Join("\n", ToolDescriptions.Where(description =>
        {
            if (allowedTools is null)
            {
                return true;
            }

            var separator = description.IndexOf('(');
            return separator > 0 && allowedTools.Contains(description[..separator]);
        }).Select(description =>
        {
            var separator = description.IndexOf('(');
            var toolName = description[..separator];
            return $"{description}\nInput JSON Schema v{EllaiToolContractCatalog.CurrentSchemaVersion}: " +
                EllaiToolContractCatalog.ToJsonSchema(toolName);
        })) + "\nOutput JSON Schema: " + EllaiToolContractCatalog.ToResultJsonSchema();

    private readonly EllaiPolicyService _policy;
    private readonly EllaiConsentService _consent;
    private readonly EllaiTechnicalDocs? _docs;
    private readonly ICaseQueueQuery? _queue;
    private readonly IEllaiApprovalStore? _receipts;
    private readonly EllaiTopicService? _topics;
    private readonly IEllaiExactReferenceIndex? _referenceIndex;
    private readonly EllaiUiAuthorityResolver _uiAuthority;
    private readonly EllaiUiRegistry? _uiRegistry;
    private readonly IEllaiUiDiscoveryService? _uiDiscovery;
    private readonly IEllaiUiObservationStore? _uiObservations;
    private readonly EllaiProductFactCatalog _productFacts;
    private readonly IGeographicRegistry? _geography;
    private readonly ICaseImportStore? _imports;
    private readonly ICaseImportWorkspaceReader? _importWorkspaces;
    private readonly SwiftAid.Application.Classification.ICaseClassificationQuery? _classification;
    private readonly SwiftAid.Application.Surveillance.ISignalReviewService? _signals;
    private readonly IDashboardAggregateQuery? _dashboard;
    private readonly OwnedCaseSpatialAnalysis? _spatial;
    private readonly IApprovedHealthSourceRegistry? _healthSources;
    private readonly ICaseTimelineQuery? _timeline;
    private readonly CaseCorrelationQuery? _correlations;
    private readonly IEllaiLocationReplayProtector? _locationReplayProtector;
    private readonly ICommunitySessionService? _communitySessions;

    /// <summary>Consent infrastructure is mandatory for consent-scoped reads.
    /// Ordinary typed messages use their own explicit classification and do
    /// not require a reasoning-text grant. The documentation source is optional: docs tools report an explicit
    /// unavailable outcome while it is unconfigured. The domain queue, topic
    /// ledger and receipt authority are optional: live projections report
    /// unavailable without them, and no Ellai-only unrestricted query is
    /// ever introduced. The UI registry, discovery service and observation
    /// channel are optional the same way: catalogue/observe report
    /// unavailable without them, never fabricated controls.</summary>
    public EllaiToolBroker(
        EllaiPolicyService policy, EllaiConsentService consent,
        EllaiTechnicalDocs? docs = null, ICaseQueueQuery? queue = null,
        IEllaiApprovalStore? receipts = null, EllaiTopicService? topics = null,
        EllaiUiRegistry? uiRegistry = null, IEllaiUiDiscoveryService? uiDiscovery = null,
        IEllaiUiObservationStore? uiObservations = null,
        EllaiProductFactCatalog? productFacts = null, IGeographicRegistry? geography = null,
        ICaseImportStore? imports = null, ICaseImportWorkspaceReader? importWorkspaces = null,
        SwiftAid.Application.Classification.ICaseClassificationQuery? classification = null,
        SwiftAid.Application.Surveillance.ISignalReviewService? signals = null,
        EllaiUiAuthorityResolver? uiAuthority = null, IEllaiExactReferenceIndex? referenceIndex = null,
        IDashboardAggregateQuery? dashboard = null, ICaseTimelineQuery? timeline = null, IEllaiLocationReplayProtector? locationReplayProtector = null,
        ICommunitySessionService? communitySessions = null, OwnedCaseSpatialAnalysis? spatial = null,
        IApprovedHealthSourceRegistry? healthSources = null, CaseCorrelationQuery? correlations = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        _docs = docs;
        _queue = queue;
        _receipts = receipts;
        _topics = topics;
        _referenceIndex = referenceIndex;
        _dashboard = dashboard;
        _spatial = spatial;
        _healthSources = healthSources;
        _timeline = timeline;
        _correlations = correlations;
        _locationReplayProtector = locationReplayProtector;
        _communitySessions = communitySessions;
        _uiAuthority = uiAuthority ?? new EllaiUiAuthorityResolver();
        _uiRegistry = uiRegistry;
        _uiDiscovery = uiDiscovery;
        _uiObservations = uiObservations;
        _productFacts = productFacts ?? new EllaiProductFactCatalog();
        _geography = geography;
        _imports = imports;
        _importWorkspaces = importWorkspaces;
        _classification = classification;
        _signals = signals;
    }

    public bool UsesStateQueue(ICaseQueueQuery? queue) => queue is not null && ReferenceEquals(_queue, queue);
    public bool UsesTopicStore(IEllaiTopicStore? store) => _topics?.UsesStore(store) == true;
    public ICaseQueueQuery? StateQueue => _queue;
    public bool UsesReferenceIndex(IEllaiExactReferenceIndex? index) => index is not null && ReferenceEquals(_referenceIndex, index);
    public bool UsesGeographicRegistry(IGeographicRegistry? registry) => registry is not null && ReferenceEquals(_geography, registry);

    public Task<EllaiToolResult> InvokeAsync(
        EllaiToolContext context, EllaiToolRequest request, CancellationToken ct) =>
        InvokeAsync(context, request, ct, null, null);

    public async Task<EllaiToolResult> InvokeAsync(
        EllaiToolContext context, EllaiToolRequest request, CancellationToken ct, ICaseQueueQuery? stateQueue,
        IEllaiExactReferenceIndex? referenceIndex = null,
        Func<EllaiToolContext, IReadOnlyList<CaseQueueItem>, CancellationToken, Task<bool>>? rememberCaseResults = null,
        IGeographicRegistry? geographicRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (!TryResolveCanonical(request.ToolName, out var canonical))
        {
            return Bound(Deny(request.ToolName, "Unregistered tool.", EllaiToolReasonCodes.UnregisteredTool));
        }

        if (context.AllowedTools is not null && !context.AllowedTools.Contains(canonical))
        {
            return Bound(Deny(request.ToolName,
                "This tool is not available in the current session profile.",
                EllaiToolReasonCodes.DeniedByPolicy));
        }

        if (context.RemainingCalls <= 0)
        {
            return Bound(Deny(request.ToolName, "Model-visible tool budget exhausted.", EllaiToolReasonCodes.BudgetExceeded));
        }

        if (string.IsNullOrWhiteSpace(context.SessionId) || string.IsNullOrWhiteSpace(context.ScopeEpoch))
        {
            return Bound(Deny(request.ToolName, "Missing session scope.", EllaiToolReasonCodes.MissingScope));
        }

        if (!EllaiToolContractCatalog.TryValidateArguments(canonical, request.Arguments, out var schemaError))
        {
            return Bound(Deny(request.ToolName, schemaError ?? "Tool arguments do not match the registered schema.",
                EllaiToolReasonCodes.InvalidArguments));
        }

        if (request.Arguments.Count > 8)
        {
            return Bound(Deny(request.ToolName, "Too many tool arguments.", EllaiToolReasonCodes.InvalidArguments));
        }

        foreach (var (key, value) in request.Arguments)
        {
            if (canonical == EllaiToolNames.ValidateObservations && key == "observations_json")
            {
                if (value is null || value.Length > BoundedCsvReader.MaximumFieldCharacters)
                    return Bound(Deny(request.ToolName, "The observations input is too large.", EllaiToolReasonCodes.InvalidArguments));
                continue;
            }
            if (value is null || value.Length > 120 || LooksLikeCode(value))
            {
                return Bound(Deny(request.ToolName, $"Argument {key} is not an allowed bounded reference.", EllaiToolReasonCodes.InvalidArguments));
            }
        }

        var ordinaryTypedMessage = string.Equals(context.ConsentPurpose,
                EllaiConsentService.PublicProductHelpPurpose, StringComparison.Ordinal)
            && string.Equals(context.DataClassification,
                EllaiProcessingClassification.UserProvidedNonSensitive, StringComparison.Ordinal);
        if (!ordinaryTypedMessage && !_consent.IsGranted(
                context.SessionId, EllaiConsentDimension.ReasoningText, context.ConsentPurpose,
                context.DataClassification, DateTimeOffset.UtcNow))
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
            return Bound(await ReadStateAsync(context, request, stateQueue ?? _queue, ct, rememberCaseResults).ConfigureAwait(false));
        }

        if (string.Equals(canonical, EllaiToolNames.Receipts, StringComparison.Ordinal))
        {
            return Bound(await ReadReceiptsAsync(context, request, ct).ConfigureAwait(false));
        }

        if (string.Equals(canonical, EllaiToolNames.Topic, StringComparison.Ordinal))
        {
            return Bound(await ReadTopicsAsync(context, request, referenceIndex ?? _referenceIndex, ct).ConfigureAwait(false));
        }

        if (string.Equals(canonical, EllaiToolNames.UiCatalogue, StringComparison.Ordinal))
        {
            return Bound(await ReadUiCatalogueAsync(context, request, ct).ConfigureAwait(false));
        }

        if (string.Equals(canonical, EllaiToolNames.UiObserve, StringComparison.Ordinal))
        {
            return Bound(await ReadUiObserveAsync(context, request, ct).ConfigureAwait(false));
        }

        if (canonical == EllaiToolNames.Locations)
            return Bound(await ReadLocationsAsync(context, request, geographicRegistry ?? _geography, ct).ConfigureAwait(false));

        if (canonical is EllaiToolNames.ImportDestinations or EllaiToolNames.Imports or EllaiToolNames.ImportStatus)
            return Bound(await ReadImportsAsync(context, request, ct).ConfigureAwait(false));
        if (canonical == EllaiToolNames.ClassificationEvidence)
            return Bound(await ReadClassificationAsync(context, request, ct).ConfigureAwait(false));
        if (canonical == EllaiToolNames.ClassificationSnapshot)
            return Bound(await ReadClassificationSnapshotAsync(context, request, ct).ConfigureAwait(false));
        if (canonical == EllaiToolNames.SurveillanceSignals)
            return Bound(await ReadSurveillanceSignalsAsync(context, request, ct).ConfigureAwait(false));
        if (canonical == EllaiToolNames.PublicSessions)
            return Bound(await ReadPublicSessionsAsync(request, ct).ConfigureAwait(false));
        if (canonical == EllaiToolNames.Evidence)
            return Bound(await ReadEvidenceAsync(context, request, referenceIndex ?? _referenceIndex, ct).ConfigureAwait(false));
        if (canonical == EllaiToolNames.OwnedCaseCount)
            return Bound(await ReadOwnedCaseCountAsync(context, request, ct).ConfigureAwait(false));
        if (canonical == EllaiToolNames.OwnedRegionCounts)
            return Bound(await ReadOwnedRegionCountsAsync(context, request, ct).ConfigureAwait(false));
        if (canonical == EllaiToolNames.ApprovedHealthSources)
            return Bound(await ReadApprovedHealthSourcesAsync(context, request, ct).ConfigureAwait(false));

        if (canonical == EllaiToolNames.CaseTimeline)
            return Bound(await ReadCaseTimelineAsync(context, request, ct).ConfigureAwait(false));

        return Bound(canonical switch
        {
            EllaiToolNames.Capabilities => Allow(request.ToolName, "Current capability descriptors.", 1, false),
            EllaiToolNames.ProductFacts => ReadProductFacts(request),
            EllaiToolNames.Now => Allow(request.ToolName, DescribeNow(DateTimeOffset.UtcNow), 1, false) with
                { HistoryReplayVersion = EllaiObservedToolResult.CurrentClockVersion },
            EllaiToolNames.DocsSearch => SearchDocs(request),
            EllaiToolNames.DocsGet => GetDocs(request),
            EllaiToolNames.IngestionSchema => ReadIngestionSchema(request),
            EllaiToolNames.ValidateObservations => ValidateObservations(request),
            _ => Deny(request.ToolName, "Unregistered tool.", EllaiToolReasonCodes.UnregisteredTool)
        });
    }

    private async Task<EllaiToolResult> ReadCaseTimelineAsync(EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
    {
        if (context.Actor is null || _timeline is null)
            return Deny(request.ToolName, "The case timeline is unavailable for this session.", EllaiToolReasonCodes.Unavailable);
        if (!request.Arguments.TryGetValue("case_id", out var value) ||
            !Guid.TryParseExact(value, "D", out var caseId) || caseId == Guid.Empty)
            return Deny(request.ToolName, "A valid case ID is required.", EllaiToolReasonCodes.InvalidArguments);
        var includeRelated = false;
        if (request.Arguments.TryGetValue("include_related", out var relatedValue) &&
            !bool.TryParse(relatedValue, out includeRelated))
            return Deny(request.ToolName, "The related-case option is invalid.", EllaiToolReasonCodes.InvalidArguments);

        CaseTimelineResult result;
        try
        {
            result = await _timeline.GetAsync(context.Actor, caseId, MaxItems, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Deny(request.ToolName, "The case timeline is unavailable.", EllaiToolReasonCodes.Unavailable);
        }
        ct.ThrowIfCancellationRequested();
        if (result.Access != CaseTimelineAccess.Found || result.Timeline is not { } timeline ||
            timeline.Header.CaseId != caseId || timeline.Header.AggregateVersion < 1)
            return Deny(request.ToolName, "The case was not found or is not accessible.", EllaiToolReasonCodes.DeniedByPolicy);

        // Timeline pages are chronological for the UI. A bounded assistant projection
        // must spend its line budget on the newest journal events first.
        var entries = timeline.Entries.Where(entry => entry.Kind == CaseTimelineEntryKind.Authoritative)
            .OrderByDescending(entry => entry.AggregateVersion).Take(MaxItems).ToArray();
        var activityCount = entries.Sum(entry => entry.AgentActivities?.Count ?? 0);
        var lines = entries.SelectMany(entry =>
            new[] { $"{entry.RecordedAtUtc:O}: {entry.Label} (v{entry.AggregateVersion})" }
                .Concat(entry.OfficerEscalation is { } officer
                    ? [$"Officer escalation follow-up assigned to {(officer.SafeOfficerDisplayLabel is { } label ? "display name (untrusted data) " + JsonSerializer.Serialize(label) : "an officer whose name is unavailable")}; " +
                       (officer.FollowUpAcknowledged == true ? "follow-up acknowledged; " :
                           $"{(officer.FollowUpAcknowledged == false ? "acknowledgment due" : officer.IsCurrent ? "acknowledgment status unavailable; original acknowledgment deadline" : "original acknowledgment deadline")} {officer.AcknowledgementDueUtc:O}; ") +
                       $"{(officer.IsCurrent ? "follow-up due" : "originally approved follow-up due")} {officer.FollowUpDueUtc:O}. " +
                       (officer.IsCurrent ? "This assignment is current at the time of this read." : "This assignment is historical; the work has changed or closed.")]
                    : Array.Empty<string>())
                .Concat((entry.AgentActivities ?? []).Take(MaxItems).Select(activity =>
                    $"{activity.RecordedAtUtc:O}: Ellai {CaseAgentActivityPresentation.RoleLabel(activity.RoleId)}: {CaseAgentActivityPresentation.ActivityLabel(activity.RoleId, activity.RecommendationKind)}." +
                    (activity.RoleId == "ops.followup" ? activity.FollowUpIsCurrent switch
                    {
                        true => " This recommendation is current at the time of this read.",
                        false => " This recommendation is historical; the follow-up has changed.",
                        _ => " Current follow-up status is unavailable."
                    } : activity.RoleId == "ops.organization" ? activity.FollowUpIsCurrent switch
                    {
                        true => " This suggestion is current at the time of this read.",
                        false => " This suggestion is historical; the case, policy or reporting window has changed.",
                        _ => " Current suggestion status is unavailable."
                    } : "") +
                    (activity.FollowUpReviewedAtUtc is { } reviewedAt ? $" Staff review recorded at {reviewedAt:O}." : ""))));
        var id = caseId.ToString("D");
        var evidence = new EvidenceRef("CaseQueue", id,
            timeline.Header.AggregateVersion.ToString(CultureInfo.InvariantCulture), null, id);
        var truncated = timeline.HasOlderEntries || activityCount + entries.Count(entry => entry.OfficerEscalation is not null) + entries.Length > MaxItems ||
            entries.Any(entry => entry.AgentActivitiesTruncated);
        var workSummary = timeline.WorkSummary is { } work
            ? $"Current case work: {work.OpenEscalations} open escalations; {work.ScheduledFollowUps} scheduled or acknowledged follow-ups" +
              (work.NextFollowUpDueUtc is { } due ? $", earliest due {due:O}. " : ". ") +
              "Counts are case-level; authorized escalation links are shown in the history when present. "
            : "Current escalation and follow-up summary is unavailable. ";
        var eventLines = lines.Take(MaxItems).ToList();
        if (includeRelated)
        {
            if (_correlations is null)
                return Deny(request.ToolName, "Related-case suggestions are unavailable.", EllaiToolReasonCodes.Unavailable);
            SwiftAid.Contracts.Cases.CaseCorrelationPage page;
            try { page = await _correlations.GetPageAsync(context.Actor, caseId, null, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { return Deny(request.ToolName, "Related-case suggestions are unavailable.", EllaiToolReasonCodes.Unavailable); }
            if (!page.Allowed) return Deny(request.ToolName, "The case was not found or is not accessible.", EllaiToolReasonCodes.DeniedByPolicy);
            var relatedLines = page.Candidates.Select(candidate =>
                $"Possible related case {candidate.RelatedCaseId:D}; review {(candidate.Decision?.ToString() ?? "Pending")}; " +
                $"recorded matching criteria: {DescribeCorrelationReasons(candidate.Reasons)}; " +
                "shared attributes do not establish identity or causation.").ToArray();
            var related = relatedLines.Length == 0
                ? new[] { "No currently accessible related-case suggestions were found." }
                : relatedLines;
            truncated |= related.Length + eventLines.Count > MaxItems || page.NextCursor is not null;
            eventLines = related.Concat(eventLines).Take(MaxItems).ToList();
        }
        var output = new EllaiToolResult(request.ToolName, true, null, [evidence],
            (includeRelated ? "Authorized possible relationships and case history, with newest events first; " : "Authorized case history, newest events first; ") +
            "sealed report content and actor identifiers are not included. " + workSummary +
            (truncated ? "Some history or activity is omitted.\n" : "\n") +
            (eventLines.Count == 0 ? "No authoritative entries were returned." : string.Join("\n", eventLines)),
            entries.Length, truncated, EllaiToolReasonCodes.Ok)
        {
            HistoryValidUntilUtc = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        return output with { CaseTimelineReplayScope = EllaiCaseTimelineReplayScope.Create(id, output, includeRelated) };
    }

    private static string DescribeCorrelationReasons(IReadOnlyList<string>? reasons)
    {
        if (reasons is null || reasons.Count == 0) return "unavailable";
        var labels = reasons.Select(reason => reason switch
        {
            nameof(CorrelationReason.SameSourceIdentifier) => "matching source identifier",
            nameof(CorrelationReason.SameSector) => "same sector",
            nameof(CorrelationReason.SharedRegion) => "shared region",
            nameof(CorrelationReason.ReportsCloseInTime) => "reports close in time",
            nameof(CorrelationReason.SemanticCandidate) => "semantic retrieval hint",
            _ => "unrecognized recorded criterion"
        }).Distinct(StringComparer.Ordinal).ToArray();
        return string.Join(", ", labels);
    }

    private async Task<EllaiToolResult> ReadApprovedHealthSourcesAsync(EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
    {
        if (context.Actor is null || _healthSources is null || _locationReplayProtector is null ||
            !request.Arguments.TryGetValue("topic", out var topic) || !request.Arguments.TryGetValue("jurisdiction", out var jurisdiction) ||
            !request.Arguments.TryGetValue("language", out var language))
            return Deny(request.ToolName, "Approved source references are unavailable.", EllaiToolReasonCodes.Unavailable);
        try
        {
            var found = await _healthSources.SearchAsync(topic, jurisdiction, language, ct).ConfigureAwait(false);
            if (!found.IsConfigured || found.Truncated || found.Sources.Count > 2 || found.Sources.Any(source =>
                source.Url.AbsoluteUri.Length > 400 || source.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 12 ||
                source.Publisher.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 8 || source.License.Length > 100))
                return Deny(request.ToolName, "A complete bounded source list is unavailable; narrow the search or check the approved registry.", EllaiToolReasonCodes.Unavailable);
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(found.Sources)));
            var reference = new EvidenceRef("HealthSourceRegistry", "approved-sources", "metadata/1", null, digest);
            var text = found.Sources.Count == 0 ? "No current approved source references match this search." :
                "Approved source references: " + string.Join("; ", found.Sources.Select(source =>
                    $"{source.Title}, {source.Publisher}, {source.Jurisdiction}, {source.Language}, version {source.Version}, license {source.License}, reviewed {source.ReviewedAtUtc:yyyy-MM-dd}, valid until {source.ValidUntilUtc:yyyy-MM-dd}: {source.Url.AbsoluteUri}")) + ".";
            var result = new EllaiToolResult(request.ToolName, true, null, [reference], text, 1, false, EllaiToolReasonCodes.Ok);
            var token = _locationReplayProtector.Protect(context, request.Arguments);
            var scope = new EllaiLocationReplayScope(token, EllaiObservedToolResult.ResultFingerprint(result));
            return scope.IsValid() ? result with { HealthSourceReplayScope = scope } :
                Deny(request.ToolName, "Approved source references are unavailable.", EllaiToolReasonCodes.Unavailable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Deny(request.ToolName, "Approved source references are unavailable.", EllaiToolReasonCodes.Unavailable); }
    }

    private async Task<EllaiToolResult> ReadOwnedRegionCountsAsync(EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
    {
        if (context.Actor is null || _spatial is null || request.Arguments.Count != 0)
            return Deny(request.ToolName, "Authorized regional counts are unavailable.", EllaiToolReasonCodes.Unavailable);
        SwiftAid.Contracts.Surveillance.SpatialAnalysisResult result;
        try { result = await _spatial.GetAsync(context.Actor, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Deny(request.ToolName, "Authorized regional counts are unavailable.", EllaiToolReasonCodes.Unavailable); }
        if (result.Access != SwiftAid.Contracts.Surveillance.SpatialAnalysisAccess.Allowed || result.Regions.Count > 20 ||
            result.Regions.Any(area => area.RegionLabel.Length > 100 || area.RegionLabel.Any(char.IsControl) ||
                area.RegionLabel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 10))
            return Deny(request.ToolName, "A complete bounded regional count is unavailable.", EllaiToolReasonCodes.Unavailable);
        var content = JsonSerializer.SerializeToUtf8Bytes(result.Regions);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        var evidence = new EvidenceRef("SpatialAggregate", "owned-regions", "region-count/1", null, digest);
        var areas = result.Regions.Count == 0 ? "No unsuppressed region counts are available" :
            string.Join("; ", result.Regions.Take(5).Select(area => $"{area.RegionLabel}: {area.CaseCount} case records"));
        var summary = $"Top {Math.Min(5, result.Regions.Count)} unsuppressed regions for readable cases owned by the current officer, created from {result.WindowStartUtc:O} to {result.WindowEndUtc:O}: {areas}. " + result.Limitations;
        return new EllaiToolResult(request.ToolName, true, null, [evidence], summary, 1, false, EllaiToolReasonCodes.Ok);
    }

    private async Task<EllaiToolResult> ReadOwnedCaseCountAsync(EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
    {
        if (context.Actor is null || _dashboard is null)
            return Deny(request.ToolName, "The authorized case count is unavailable.", EllaiToolReasonCodes.Unavailable);
        if (!request.Arguments.TryGetValue("window_days", out var value) ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days is < 1 or > 365)
            return Deny(request.ToolName, "Choose a window of 1 to 365 days.", EllaiToolReasonCodes.InvalidArguments);

        DashboardAggregateResult result;
        try { result = await _dashboard.GetAsync(context.Actor, TimeSpan.FromDays(days), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Deny(request.ToolName, "The authorized case count is unavailable.", EllaiToolReasonCodes.Unavailable);
        }
        ct.ThrowIfCancellationRequested();
        if (result.Access != DashboardAggregateAccess.Allowed || result.Truncated)
            return Deny(request.ToolName, "A complete authorized case count is unavailable for this window.", EllaiToolReasonCodes.Unavailable);

        var asOf = result.AsOfUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var since = result.AsOfUtc.ToUniversalTime().AddDays(-days).ToString("O", CultureInfo.InvariantCulture);
        var evidence = new EvidenceRef("DashboardAggregate", "owned-cases", asOf, null, Guid.NewGuid().ToString("N"));
        var summary = $"{result.Rows.Count} managed case(s) currently owned by the requesting officer and readable now, " +
            $"created on or after {since} (a {days}-day lookback; query started {asOf}). " +
            "Unit: managed case records; not incidence or animal counts.";
        return new EllaiToolResult(request.ToolName, true, null, [evidence], summary, 1, false, EllaiToolReasonCodes.Ok)
        {
            CountReplayScope = EllaiCountReplayScope.Create(days, result.Rows.Select(row => row.CaseId))
        };
    }

    // Update the revision whenever the schema content changes.
    internal static EvidenceRef IngestionSchemaEvidence { get; } =
        new("ProductFact", "ingestion-schema", "1", "capture", "ingestion-schema/1");

    private static EllaiToolResult ReadIngestionSchema(EllaiToolRequest request)
    {
        if (request.Arguments.Count != 0)
            return Deny(request.ToolName, "This tool takes no arguments.", EllaiToolReasonCodes.InvalidArguments);
        var summary = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, columns = CaseCsvImportAdapter.Columns,
            observations = "observations_json is an array of up to 32 original strings, each at most 300 characters; never objects or inferred diagnoses.",
            capture = "Preserve source narrative, occurrence/capture timestamps and explicit unknowns. Dates with a time require an offset. Ask for missing facts without inventing them.",
            authority = "Public intake returns a private unverified receipt. Managed-case capture requires current staff authorization and explicit report review. A source case_id is not the destination case ID. Empty workspace/owner fields use the authorized import context and submitting officer.",
            submission = "Use the existing report form for individual capture or Workspace > Imports for authorized case CSV imports. These tools prepare, validate or read progress; never claim a report was saved without its durable receipt.",
            provenance = "CaseCsvImportAdapter/v1"
        });
        return new(request.ToolName, true, null,
            [IngestionSchemaEvidence], summary, 1, false);
    }

    private async Task<EllaiToolResult> ReadEvidenceAsync(
        EllaiToolContext context, EllaiToolRequest request, IEllaiExactReferenceIndex? referenceIndex, CancellationToken ct)
    {
        if (!string.Equals(request.Arguments["ref"], EllaiCaseResultMemory.Reference, StringComparison.Ordinal))
        {
            return Deny(request.ToolName, "Only the current session's retained case-results reference is supported.",
                EllaiToolReasonCodes.InvalidArguments);
        }

        if (context.Actor is null)
        {
            return Deny(request.ToolName, "Current staff case access is required.",
                EllaiToolReasonCodes.DeniedByPolicy);
        }

        if (_topics is null || _queue is null)
        {
            return Deny(request.ToolName, "Authorized case evidence is unavailable for this session.",
                EllaiToolReasonCodes.Unavailable);
        }

        var field = request.Arguments.GetValueOrDefault("field");
        if (field is not null && field is not ("country" or "workflow"))
        {
            return Deny(request.ToolName, "field must be country or workflow.",
                EllaiToolReasonCodes.InvalidArguments);
        }

        try
        {
            var result = await new EllaiCaseResultMemory(_topics, _queue, referenceIndex)
                .RecallAsync(context, ct, field).ConfigureAwait(false);
            return result with { ToolName = request.ToolName };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            return Deny(request.ToolName, "Authorized case evidence is unavailable for this session.",
                EllaiToolReasonCodes.Unavailable);
        }
    }

    private async Task<EllaiToolResult> ReadPublicSessionsAsync(EllaiToolRequest request, CancellationToken ct)
    {
        if (_communitySessions is null)
            return Deny(request.ToolName, "Public sessions are unavailable.", EllaiToolReasonCodes.Unavailable);
        try
        {
            const int limit = 10;
            request.Arguments.TryGetValue("status", out var status);
            var page = await _communitySessions.QueryPublicAsync(new PublicSessionQuery(Limit: limit, Status: status), ct).ConfigureAwait(false);
            var items = page.Items;
            var evidence = items.Count == 0
                ? new[] { new EvidenceRef("PublicSessionSearch", "public-sessions", "1", "search", "public-sessions/1") }
                : items.Select(item => new EvidenceRef("PublicSession", item.SessionId.ToString("D"),
                    item.Version.ToString(CultureInfo.InvariantCulture), "public-session", item.SessionId.ToString("D"))).ToArray();
            var continuation = page.Next is null ? string.Empty : " (more sessions may be available)";
            var searchScope = status is null ? "public sessions" : $"{status.ToLowerInvariant()} public sessions";
            var filter = status is null ? string.Empty : $"; search filter {status}";
            var lines = items.Select(item => $"session {item.SessionId:D} · title {JsonSerializer.Serialize(item.Title)} · starts {item.StartUtc:O} · status {item.Status}{filter}{continuation}");
            return new EllaiToolResult(request.ToolName, true, null, evidence,
                items.Count == 0 ? (page.Next is null ? $"No {searchScope} were found." :
                    $"No {searchScope} were found in this search; more may be available.") : string.Join("\n", lines),
                items.Count, page.Next is not null, EllaiToolReasonCodes.Ok);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Deny(request.ToolName, "Public sessions are unavailable.", EllaiToolReasonCodes.Unavailable); }
    }

    private async Task<EllaiToolResult> ReadSurveillanceSignalsAsync(EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
    {
        if (context.Actor is null || _signals?.Enabled != true)
            return Deny(request.ToolName, "Surveillance information is unavailable.", EllaiToolReasonCodes.Unavailable);
        if (!request.Arguments.TryGetValue("case_id", out var rawCaseId) ||
            !Guid.TryParseExact(rawCaseId, "D", out var caseId) || caseId == Guid.Empty)
            return Deny(request.ToolName, "A valid case ID is required.", EllaiToolReasonCodes.InvalidArguments);
        try
        {
            // The service independently requires current access to every contributing case.
            var signals = await _signals.ForCaseAsync(context.Actor, caseId, ct).ConfigureAwait(false);
            if (signals is not { Count: > 0 and <= 10 })
                return Deny(request.ToolName, "A bounded authorized signal result is unavailable.", EllaiToolReasonCodes.Unavailable);
            var evidence = signals.Select(signal => new EvidenceRef("SurveillanceSignal", caseId.ToString("D"),
                signal.Version.ToString(CultureInfo.InvariantCulture), "signal", signal.SignalId.ToString("D"))).ToArray();
            var lines = signals.Select(signal => $"case {caseId:D} signal {signal.SignalId:D}: " +
                JsonSerializer.Serialize(new { signal.State, signal.Version, signal.Assessment.Method,
                    signal.Assessment.Area, signal.Assessment.Syndrome, signal.Assessment.WindowStartUtc,
                    signal.Assessment.WindowEndUtc, signal.Assessment.Observed, signal.Assessment.ReportingActivity,
                    signal.Assessment.IndependentSources, signal.Assessment.Expected, signal.Assessment.Comparison,
                    signal.Assessment.Candidate, signal.Assessment.Limitations }) +
                (signal.Assessment.Method.Contains("synthetic", StringComparison.OrdinalIgnoreCase)
                    ? "; synthetic evaluation method; not evidence of a validated real-world detector"
                    : "; method-specific validation must be reviewed") +
                "; report-based review signal, not prevalence, a diagnosis, or a confirmed outbreak; other signals may exist.");
            return new EllaiToolResult(request.ToolName, true, null, evidence, string.Join("\n", lines), signals.Count,
                false, EllaiToolReasonCodes.Ok);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Deny(request.ToolName, "Surveillance information is unavailable.", EllaiToolReasonCodes.Unavailable); }
    }

    private async Task<EllaiToolResult> ReadClassificationSnapshotAsync(EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
    {
        if (context.Actor is null || _classification is null)
            return Deny(request.ToolName, "Classification information is unavailable.", EllaiToolReasonCodes.Unavailable);
        if (!request.Arguments.TryGetValue("case_id", out var rawCaseId) ||
            !Guid.TryParseExact(rawCaseId, "D", out var caseId) || caseId == Guid.Empty)
            return Deny(request.ToolName, "A valid case ID is required.", EllaiToolReasonCodes.InvalidArguments);
        try
        {
            var records = await _classification.GetAsync(context.Actor, caseId, ct).ConfigureAwait(false);
            if (records is null)
                return Deny(request.ToolName, "The case was not found or is not accessible.", EllaiToolReasonCodes.DeniedByPolicy);
            if (records.Count is 0 or > 10)
                return Deny(request.ToolName, "A bounded classification record is unavailable for this case.", EllaiToolReasonCodes.Unavailable);
            var evidence = records.Select(record => new EvidenceRef("CaseClassification", caseId.ToString("D"),
                record.ReportRevision.ToString(CultureInfo.InvariantCulture), "classification", record.ResultId.ToString("D"))).ToArray();
            var lines = records.Select(record => $"case {caseId:D} classification {record.ResultId:D}: " +
                JsonSerializer.Serialize(new { record.State, record.CurrentConfiguration, record.ReportRevision,
                    record.ModelId, record.ModelRevision, record.TaxonomyVersion, record.AbstentionReason,
                    selectedLabels = record.Labels.Where(label => label.Selected).Select(label => label.Id).ToArray() }) +
                "; uncalibrated reported-text patterns, not a diagnosis or confirmed incident; other records may exist.");
            return new EllaiToolResult(request.ToolName, true, null, evidence, string.Join("\n", lines), records.Count, false,
                EllaiToolReasonCodes.Ok);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Deny(request.ToolName, "Classification information is unavailable.", EllaiToolReasonCodes.Unavailable); }
    }

    private async Task<EllaiToolResult> ReadClassificationAsync(EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
    {
        if (context.Actor is null) return Deny(request.ToolName, "Current staff case access is required.", EllaiToolReasonCodes.DeniedByPolicy);
        if (request.Arguments.Count != 1 || !request.Arguments.TryGetValue("case_id", out var value) || !Guid.TryParse(value, out var caseId))
            return Deny(request.ToolName, "Supply one case_id.", EllaiToolReasonCodes.InvalidArguments);
        if (_classification is null) return Deny(request.ToolName, "Classification evidence is unavailable.", EllaiToolReasonCodes.Unavailable);
        try
        {
            var items = await _classification.GetAsync(context.Actor, caseId, ct).ConfigureAwait(false);
            var signals = items is not null && _signals?.Enabled == true
                ? await _signals.ForCaseAsync(context.Actor, caseId, ct).ConfigureAwait(false) : null;
            return items is null ? Deny(request.ToolName, "That case is unavailable.", EllaiToolReasonCodes.DeniedByPolicy) :
                new(request.ToolName, true, null, [], JsonSerializer.Serialize(new { items, signals,
                    interpretation = "Uncalibrated semantic patterns in reported evidence. Not clinical classification, diagnosis, urgency or outbreak confirmation. No new inference was performed." }), 1, false);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        { return Deny(request.ToolName, "Classification evidence is unavailable.", EllaiToolReasonCodes.Unavailable); }
    }

    private async Task<EllaiToolResult> ReadImportsAsync(EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
    {
        if (context.Actor is null)
            return Deny(request.ToolName, "Import records require current staff access.", EllaiToolReasonCodes.DeniedByPolicy);
        if (_imports is null || _importWorkspaces is null)
            return Deny(request.ToolName, "Import information is unavailable.", EllaiToolReasonCodes.Unavailable);
        try
        {
            if (request.ToolName == EllaiToolNames.ImportDestinations)
            {
                if (request.Arguments.Count != 0)
                    return Deny(request.ToolName, "This tool takes no arguments.", EllaiToolReasonCodes.InvalidArguments);
                var destinations = await _importWorkspaces.GetWorkspacesAsync(context.Actor, ct).ConfigureAwait(false);
                var items = destinations.Take(MaxItems).ToArray();
                return new(request.ToolName, true, null, [],
                    JsonSerializer.Serialize(new { destinations = items, importPage = "/workspace/imports", submitted = false }),
                    items.Length, destinations.Count > items.Length);
            }
            if (request.ToolName == EllaiToolNames.Imports)
            {
                if (request.Arguments.Count != 3 ||
                    !request.Arguments.TryGetValue("workspace_id", out var workspace) || string.IsNullOrWhiteSpace(workspace) ||
                    !request.Arguments.TryGetValue("organization_id", out var organization) || string.IsNullOrWhiteSpace(organization) ||
                    !request.Arguments.TryGetValue("country", out var country) || country.Length != 2 || country.Any(character => character is < 'A' or > 'Z'))
                    return Deny(request.ToolName, "Supply the organization, workspace and country from a permitted import destination.", EllaiToolReasonCodes.InvalidArguments);
                var history = await _importWorkspaces.GetRecentAsync(context.Actor, workspace, country, ct, organization).ConfigureAwait(false);
                var items = history.Take(MaxItems).ToArray();
                return new(request.ToolName, true, null,
                    items.Select(item => new EvidenceRef("ImportJob", item.ImportId.ToString("D"), item.UpdatedAtUtc.ToString("O"), "status", item.ImportId.ToString("D"))).ToArray(),
                    JsonSerializer.Serialize(new { imports = items, importPage = "/workspace/imports" }), items.Length, history.Count >= MaxItems);
            }
            if (request.Arguments.Count != 1 || !request.Arguments.TryGetValue("import_id", out var importText) ||
                !Guid.TryParseExact(importText, "D", out var importId) || importId == Guid.Empty)
                return Deny(request.ToolName, "Supply a complete import reference.", EllaiToolReasonCodes.InvalidArguments);
            var summary = await _imports.GetAsync(context.Actor, importId, ct).ConfigureAwait(false);
            if (summary is null)
                return Deny(request.ToolName, "That import is not available to the current user.", EllaiToolReasonCodes.DeniedByPolicy);
            return new(request.ToolName, true, null,
                [new EvidenceRef("ImportJob", importId.ToString("D"), summary.UpdatedAtUtc.ToString("O"), "status", importId.ToString("D"))],
                JsonSerializer.Serialize(new { import = summary, intelligenceStatus = "not-reported-by-import-ledger",
                    interpretation = "Completed means the import file was processed. Check rejected and retry counts. It does not mean surveillance analysis finished." }), 1, false);
        }
        catch (CaseImportException exception) when (exception.Code == "not-authorized")
        {
            return Deny(request.ToolName, "That import scope is not available to the current user.", EllaiToolReasonCodes.DeniedByPolicy);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return Deny(request.ToolName, "Import information is unavailable.", EllaiToolReasonCodes.Unavailable);
        }
    }

    private static EllaiToolResult ValidateObservations(EllaiToolRequest request)
    {
        if (request.Arguments.Count != 1 || !request.Arguments.TryGetValue("observations_json", out var json))
            return Deny(request.ToolName, "Supply observations_json as an array of reported strings.", EllaiToolReasonCodes.InvalidArguments);
        try
        {
            var observations = CaseCsvImportAdapter.ParseObservations(json);
            // Do not echo source text into logs/progress or turn validation into clinical interpretation.
            return Allow(request.ToolName, $"Valid observations format: {observations.Count} original strings. Nothing has been submitted.", 1, false);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return Deny(request.ToolName, "Use a JSON array of up to 32 nonempty strings, each at most 300 characters.", EllaiToolReasonCodes.InvalidArguments);
        }
    }

    private async Task<EllaiToolResult> ReadLocationsAsync(EllaiToolContext context, EllaiToolRequest request,
        IGeographicRegistry? geography, CancellationToken ct)
    {
        if (request.Arguments.Keys.Any(key => key is not ("q" or "country" or "limit")) ||
            !request.Arguments.TryGetValue("q", out var query) || string.IsNullOrWhiteSpace(query))
            return Deny(request.ToolName, "Supply a place name to search.", EllaiToolReasonCodes.InvalidArguments);
        var limit = 5;
        if (request.Arguments.TryGetValue("limit", out var text) && (!int.TryParse(text, out limit) || limit is < 1 or > 10))
            return Deny(request.ToolName, "Use a limit from 1 to 10.", EllaiToolReasonCodes.InvalidArguments);
        request.Arguments.TryGetValue("country", out var country);
        if (country is not null && (country.Length != 2 || country.Any(character => character is < 'A' or > 'Z')))
            return Deny(request.ToolName, "Use a two-letter uppercase country code.", EllaiToolReasonCodes.InvalidArguments);
        if (geography is null) return Deny(request.ToolName, "Location search is unavailable.", EllaiToolReasonCodes.Unavailable);
        try
        {
            var matches = await geography.SearchAsync(query, country, limit, ct).ConfigureAwait(false);
            var items = matches.Results.Take(limit).Select(item => new { item.Id, item.Name, item.CountryCode, item.Ancestors }).ToArray();
            var result = new EllaiToolResult(request.ToolName, true, null,
                items.Select(item => new EvidenceRef("GeographicRegistry", item.Id, matches.RegistryVersion, "name", item.Id)).ToArray(),
                JsonSerializer.Serialize(new { matches.RegistryVersion, matches = items, requiresUserSelection = true }),
                items.Length, matches.TotalCount > items.Length);
            if (!result.Truncated && _locationReplayProtector is not null)
            {
                var token = _locationReplayProtector.Protect(context, request.Arguments);
                var scope = new EllaiLocationReplayScope(token, EllaiObservedToolResult.ResultFingerprint(result));
                if (scope.IsValid()) result = result with { LocationReplayScope = scope };
            }
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return Deny(request.ToolName, "Location search is unavailable.", EllaiToolReasonCodes.Unavailable);
        }
    }

    /// <summary>Live <c>ellai_state</c> projections. Without a projection
    /// argument the static permitted-feature description applies. The
    /// <c>queue</c> projection runs the ordinary scoped "my cases" query for
    /// the trusted actor: same handlers, same per-case authorization, no
    /// Ellai-only query path.</summary>
    private async Task<EllaiToolResult> ReadStateAsync(
        EllaiToolContext context, EllaiToolRequest request, ICaseQueueQuery? queue, CancellationToken ct,
        Func<EllaiToolContext, IReadOnlyList<CaseQueueItem>, CancellationToken, Task<bool>>? rememberCaseResults)
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

        if (context.Actor is null || queue is null)
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

        if (request.Arguments.Keys.Any(key => key is not ("projection" or "limit" or "country" or "workflow" or "sort")))
            return Deny(request.ToolName, "This queue supports country, workflow and sort filters only.",
                EllaiToolReasonCodes.InvalidArguments);

        request.Arguments.TryGetValue("country", out var country);
        request.Arguments.TryGetValue("workflow", out var workflow);
        request.Arguments.TryGetValue("sort", out var sort);
        if ((country is not null && (country.Length != 2 || country.Any(c => c is < 'A' or > 'Z')))
            || (workflow is not null && workflow is not ("Reported" or "UnderReview" or "Active" or "AwaitingFollowUp" or "Resolved"))
            || (sort is not null && sort is not ("latest-reported" or "latest-updated")))
            return Deny(request.ToolName, "Use a two-letter country, a recorded workflow and latest-reported or latest-updated sort.",
                EllaiToolReasonCodes.InvalidArguments);

        CaseQueueResult result;
        try
        {
            result = await queue.GetMineAsync(context.Actor, limit,
                new CaseQueueFilter(country, workflow, sort != "latest-updated"), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Deny(request.ToolName,
                "The work queue could not be read right now.",
                EllaiToolReasonCodes.Unavailable);
        }

        ct.ThrowIfCancellationRequested();

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
        var recallNotice = "";
        if (_topics is not null)
        {
            try
            {
                var retained = rememberCaseResults is null
                    ? await RememberDirectlyAsync().ConfigureAwait(false)
                    : await rememberCaseResults(context, items, ct).ConfigureAwait(false);
                recallNotice = retained
                    ? " Result references retained for this session; recall with ellai_topic(ref=case-results)."
                    : " Result references could not be retained; do not claim they were remembered.";

                async Task<bool> RememberDirectlyAsync()
                {
                    await new EllaiCaseResultMemory(_topics, queue, _referenceIndex).RememberAsync(context, items, ct).ConfigureAwait(false);
                    return true;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                recallNotice = " Result references could not be retained; do not claim they were remembered.";
            }
        }
        var lines = items.Select(item =>
            $"case {item.CaseId:D} · workflow {item.Workflow}; country {item.CountryCode} · v{item.AggregateVersion} · updated {item.UpdatedAtUtc:O}" +
            (items.Count >= limit ? " · list may be incomplete" : ""));
        var evidence = items.Select(item => new EvidenceRef(
            "CaseQueue", item.CaseId.ToString("D"), item.AggregateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), null,
            item.CaseId.ToString("D"))).ToList();
        return new EllaiToolResult(request.ToolName, true, null, evidence,
            "Owned cases only; review status is not supplied." + recallNotice + "\n" +
            (items.Count == 0 ? "The permitted work queue is empty." : string.Join("\n", lines)),
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
        if (list.Any(receipt => receipt is null ||
            !string.Equals(receipt.SessionId, context.SessionId, StringComparison.Ordinal)))
        {
            return Deny(request.ToolName, "Receipt authority is unavailable for this session.",
                EllaiToolReasonCodes.Unavailable);
        }
        var items = list.Take(MaxItems).ToList();
        var lines = items.Select(DescribeReceipt);
        var refs = items.Select(receipt => new EvidenceRef(
            "Receipt", receipt.ReceiptId, receipt.Status.ToString(), null, receipt.ReceiptId)).ToList();
        if (items.Count == 0)
            refs.Add(new EvidenceRef("ReceiptSearch", "session-receipts", "1", "search", "session-receipts/1"));
        return new EllaiToolResult(request.ToolName, true, null, refs,
            items.Count == 0 ? "No receipts recorded in this session. This does not prove that an action did not happen." : string.Join("\n", lines),
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

    /// <summary>Live <c>ellai_topic</c> resolution against the session's
    /// durable topic history: ordered mentions resolve first, multiple
    /// matches stay explicitly ambiguous, and unknown references are an
    /// authorized empty. Topics never leak across sessions.</summary>
    private async Task<EllaiToolResult> ReadTopicsAsync(
        EllaiToolContext context, EllaiToolRequest request, IEllaiExactReferenceIndex? referenceIndex, CancellationToken ct)
    {
        if (_topics is null)
        {
            return Deny(request.ToolName,
                "The topic ledger is not available for this session.",
                EllaiToolReasonCodes.Unavailable);
        }

        if (!request.Arguments.TryGetValue("ref", out var reference)
            || string.IsNullOrWhiteSpace(reference))
        {
            return Deny(request.ToolName,
                "A bounded ref argument is required to resolve a topic.",
                EllaiToolReasonCodes.InvalidArguments);
        }

        if (string.Equals(reference.Trim(), EllaiCaseResultMemory.Reference, StringComparison.OrdinalIgnoreCase))
        {
            if (_queue is null) return Deny(request.ToolName, "Case recall is unavailable.", EllaiToolReasonCodes.Unavailable);
            try
            {
                return await new EllaiCaseResultMemory(_topics, _queue, referenceIndex).RecallAsync(context, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return Deny(request.ToolName, "Case recall is unavailable. Try the search again.", EllaiToolReasonCodes.Unavailable);
            }
        }
        var (resolved, ambiguous) = await _topics
            .ResolveAsync(context.SessionId, reference.Trim(), ct).ConfigureAwait(false);
        // Reserved snapshots must never escape through generic topic/ordinal lookup.
        var matches = resolved.Where(topic => topic.Title != EllaiCaseResultMemory.TopicTitle).Take(MaxItems).ToList();
        if (matches.Count == 0)
        {
            return Allow(request.ToolName, "No permitted topic matches the reference.", 0, false);
        }

        var lines = matches.Select(topic =>
            $"topic {topic.TopicId} · {topic.Title}" +
            (topic.EntityRef is null ? string.Empty : $" · ref {topic.EntityRef}") +
            (topic.EntityVersion is null ? string.Empty : $" · v{topic.EntityVersion}") +
            (topic.CheckpointId is null ? string.Empty : $" · checkpoint {topic.CheckpointId}"));
        var evidence = matches.Select(topic => new EvidenceRef(
            "Topic", topic.TopicId, topic.EntityVersion ?? topic.CoverageState, null, topic.TopicId)).ToList();
        var summary = (ambiguous ? "Ambiguous reference — candidates: " : string.Empty)
            + string.Join("\n", lines);
        return new EllaiToolResult(request.ToolName, true, null, evidence,
            summary, matches.Count, false, EllaiToolReasonCodes.Ok);
    }

    private static string? ResolveUiFeature(EllaiToolContext context, EllaiToolRequest request, EllaiUiEntitlements authority)
    {
        var defaultFeature = context.FeatureId == "public.product-help" ? EllaiReportUiManifest.FeatureId : context.FeatureId;
        var requested = request.Arguments.GetValueOrDefault("feature", defaultFeature);
        // Existing feature-specific sessions keep their metadata boundary. New capabilities
        // require fresh server authority, including when the session itself names navigation.
        if (requested is EllaiNavigationManifest.FeatureId or EllaiDashboardUiManifest.FeatureId or EllaiCaseListUiManifest.FeatureId or EllaiCaseDetailUiManifest.FeatureId or EllaiSurveillanceUiManifest.FeatureId or EllaiInvestigationUiManifest.FeatureId or EllaiMonitoringUiManifest.FeatureId or EllaiOrganizationUiManifest.FeatureId)
            return authority.Features.Contains(requested) ? requested : null;
        return requested == defaultFeature || authority.Features.Contains(requested) ? requested : null;
    }

    private async Task<EllaiToolResult> ReadUiCatalogueAsync(EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
    {
        var authority = await _uiAuthority.ResolveAsync(context.Actor, context.ScopeEpoch, ct).ConfigureAwait(false);
        var feature = ResolveUiFeature(context, request, authority);
        if (feature is null || request.Arguments.Keys.Any(key => key is not ("feature" or "goal" or "limit")))
            return Deny(request.ToolName, "The requested UI feature or argument is not available in this scope.", EllaiToolReasonCodes.InvalidArguments);
        if (_uiRegistry is null)
        {
            return Deny(request.ToolName,
                "The UI catalogue is not available for this session.",
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

        var goalTags = request.Arguments.TryGetValue("goal", out var goal) && !string.IsNullOrWhiteSpace(goal)
            ? new[] { goal.Trim() }
            : Array.Empty<string>();
        var snapshot = _uiRegistry.GetSnapshot(
            new UiRegistryQuery(feature, goalTags, limit),
            new HashSet<string>(StringComparer.Ordinal) { feature },
            authority.Capabilities);
        var unmatchedGoal = goalTags.Length > 0 && snapshot.Descriptors.Count == 0;
        if (unmatchedGoal)
        {
            // Goal tags are discovery hints, not permissions. An unfamiliar
            // phrase must not hide the eligible feature or imply access denial.
            snapshot = _uiRegistry.GetSnapshot(new UiRegistryQuery(feature, [], limit),
                new HashSet<string>(StringComparer.Ordinal) { feature }, authority.Capabilities);
        }
        var items = snapshot.Descriptors.Take(limit).ToList();
        if (items.Count == 0)
        {
            var empty = Allow(request.ToolName, "No entitled controls are registered for this feature.", 0, false);
            return goalTags.Length == 0 ? empty with { UiCatalogueReplayScope = EllaiUiCatalogueReplayScope.Create(feature, limit, empty) } : empty;
        }

        var lines = items.Select(d =>
            $"control {d.ControlId} · {d.ElementKind} · v{d.DescriptorVersion} · risk {d.RiskClass}" +
            (d.ObjectiveId is null ? string.Empty : $" · objective {d.ObjectiveId}") +
            $"; feature {d.FeatureId}; navigation {d.NavigationId}; purpose {d.Semantics?.Purpose}" +
            $"; human confirmation {d.HumanConfirmationRequired}; goal tags {string.Join(",", d.GoalTags)}");
        var evidence = items.Select(d => new EvidenceRef(
            "UiCatalogue", d.ControlId, d.DescriptorVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture), null, d.ControlId)).ToList();
        var summary = $"Semantic catalogue {snapshot.CatalogueVersion} " +
            $"contract {snapshot.UiContractVersion}: {items.Count} entitled control(s)" +
            (snapshot.Truncated ? " (truncated)." : ".") +
            (unmatchedGoal ? " No exact goal tag matched; showing the bounded entitled feature catalogue instead." : string.Empty) +
            "\n" + string.Join("\n", lines);
        var result = new EllaiToolResult(request.ToolName, true, null, evidence,
            summary, items.Count, snapshot.Truncated, EllaiToolReasonCodes.Ok);
        // Only retain a goal when it exactly matches a server-owned descriptor tag.
        // Arbitrary model text is never copied into durable replay provenance.
        var replayGoal = goalTags.Length == 1 && !unmatchedGoal
            ? items.SelectMany(item => item.GoalTags).FirstOrDefault(tag => tag == goalTags[0]) : null;
        return !result.Truncated && (goalTags.Length == 0 || replayGoal is not null)
            ? result with { UiCatalogueReplayScope = EllaiUiCatalogueReplayScope.Create(feature, limit, result, replayGoal) }
            : result;
    }

    /// <summary>Live <c>ellai_ui_observe</c> through the client-posted
    /// mounted-control channel. Ready only for entitled controls the latest
    /// session report shows mounted; everything else snapshot-entitled is
    /// not assumed. Unknown control references fail closed; a missing
    /// report is an authorized empty, never a fabricated view.</summary>
    private async Task<EllaiToolResult> ReadUiObserveAsync(
        EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
    {
        var authority = await _uiAuthority.ResolveAsync(context.Actor, context.ScopeEpoch, ct).ConfigureAwait(false);
        var feature = ResolveUiFeature(context, request, authority);
        if (feature is null || request.Arguments.Keys.Any(key => key is not ("feature" or "controls" or "view" or "context")))
            return Deny(request.ToolName, "The requested UI feature or argument is not available in this scope.", EllaiToolReasonCodes.InvalidArguments);
        if (_uiRegistry is null || _uiDiscovery is null || _uiObservations is null)
        {
            return Deny(request.ToolName,
                "Live UI observation is not available for this session.",
                EllaiToolReasonCodes.Unavailable);
        }

        if (!request.Arguments.TryGetValue("controls", out var rawControls)
            || string.IsNullOrWhiteSpace(rawControls))
        {
            return Deny(request.ToolName,
                "A bounded controls argument (comma-separated control references) is required.",
                EllaiToolReasonCodes.InvalidArguments);
        }

        var requested = rawControls.Split(',')
            .Select(id => id.Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxItems + 1)
            .ToList();
        if (requested.Count == 0 || requested.Count > MaxItems)
        {
            return Deny(request.ToolName,
                $"controls must name from 1 to {MaxItems} control references.",
                EllaiToolReasonCodes.InvalidArguments);
        }

        if (!request.Arguments.TryGetValue("view", out var view) || string.IsNullOrWhiteSpace(view))
        {
            return Deny(request.ToolName,
                "A view argument (current view/instance context) is required.",
                EllaiToolReasonCodes.InvalidArguments);
        }

        var report = await _uiObservations.LatestAsync(context.SessionId, ct).ConfigureAwait(false);
        var goalTags = request.Arguments.TryGetValue("context", out var goalContext)
            && !string.IsNullOrWhiteSpace(goalContext)
            ? goalContext.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList()
            : new List<string>();
        var snapshot = _uiRegistry.GetSnapshot(
            new UiRegistryQuery(feature, goalTags, MaxItems),
            new HashSet<string>(StringComparer.Ordinal) { feature },
            authority.Capabilities);
        var eligible = snapshot.Descriptors.ToDictionary(d => d.ControlId, StringComparer.Ordinal);
        var unknown = requested.Where(id => !eligible.ContainsKey(id)).ToList();
        var knownGoals = snapshot.Descriptors.SelectMany(descriptor => descriptor.GoalTags).ToHashSet(StringComparer.Ordinal);
        if (report is null)
        {
            var empty = Allow(request.ToolName, "No client UI observation has been reported for this session.", 0, false);
            var replay = new EllaiUiObservationReplayScope(feature, string.Join(",", requested),
                goalTags.Count == 0 ? null : string.Join(",", goalTags),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("no-client-report"u8)));
            return unknown.Count == 0 && goalTags.All(knownGoals.Contains) && replay.IsValid()
                ? empty with { UiObservationReplayScope = replay } : empty;
        }

        if (view == "current") view = report.ViewInstanceId;
        if (unknown.Count > 0)
        {
            return Deny(request.ToolName,
                $"Unknown or ineligible control reference(s): {string.Join(", ", unknown)}.",
                EllaiToolReasonCodes.InvalidArguments);
        }

        var now = DateTimeOffset.UtcNow;
        var mounted = new HashSet<string>(report.MountedControlIds, StringComparer.Ordinal);
        var observations = _uiDiscovery.Observe(
            snapshot, mounted, report.ViewInstanceId, report.ContextStamp, report.Platform, report.ReceivedAtUtc,
            report.ControlStates);
        var byId = observations.ToDictionary(o => o.ControlId, StringComparer.Ordinal);
        var age = now - report.ReceivedAtUtc;
        var stale = age < TimeSpan.Zero || age > TimeSpan.FromSeconds(45) ||
            !string.Equals(report.ViewInstanceId, view.Trim(), StringComparison.Ordinal);
        var ordered = requested.Select(id => stale ? byId[id] with { State = UiObservationState.Stale } : byId[id]).ToList();
        var lines = ordered.Select(o =>
            $"control {o.ControlId} · {o.State} · view {o.ViewInstanceId} · {o.Platform}" +
            $"; client form validation accepted {!stale && eligible[o.ControlId].ProofPolicyId == "proof-ui-validated" && report.CompletedControlIds?.Contains(o.ControlId) == true}");
        var evidence = ordered.Select(o => new EvidenceRef(
            "UiObservation", o.ControlId, o.State.ToString(), null, o.ControlId)).ToList();
        var header = $"ClientObservation: {ordered.Count} control(s) from view " +
            $"{report.ViewInstanceId} ({report.ContextStamp}) reported " +
            $"{Math.Max(0, (long)age.TotalSeconds)}s ago on {report.Platform}." +
            (string.Equals(report.ViewInstanceId, view.Trim(), StringComparison.Ordinal)
                ? string.Empty
                : $" Requested view '{view.Trim()}' differs from the latest report.");
        var result = new EllaiToolResult(request.ToolName, true, null, evidence,
            header + "\n" + string.Join("\n", lines),
            ordered.Count, false, EllaiToolReasonCodes.Ok);
        // Persist only registry-owned references, never arbitrary model text or
        // client report contents. A fresh call must resolve the same report.
        if (string.Equals(report.ViewInstanceId, view.Trim(), StringComparison.Ordinal) && goalTags.All(knownGoals.Contains))
        {
            // The receipt time changes on every heartbeat. Freshness is already
            // reflected in the observed states and validation flags in lines.
            var content = JsonSerializer.SerializeToUtf8Bytes(new
            {
                report.ViewInstanceId, report.ContextStamp, report.Platform,
                Lines = lines.ToArray(), Evidence = evidence
            });
            var replay = new EllaiUiObservationReplayScope(feature, string.Join(",", requested),
                goalTags.Count == 0 ? null : string.Join(",", goalTags),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)));
            if (replay.IsValid()) result = result with { UiObservationReplayScope = replay };
        }
        return result;
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
        var evidence = hits.Select(hit => new EvidenceRef("Documentation", hit.SectionId,
            hit.Revision, null, hit.SectionId)).ToArray();
        return new EllaiToolResult(request.ToolName, true, null, evidence,
            string.Join("\n", lines), hits.Count, false, EllaiToolReasonCodes.Ok);
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
        return new EllaiToolResult(request.ToolName, true, null,
            [new EvidenceRef("Documentation", section.SectionId, section.Revision, null, section.SectionId)],
            summary, 1, false, EllaiToolReasonCodes.Ok);
    }

    private EllaiToolResult ReadProductFacts(EllaiToolRequest request)
    {
        if (request.Arguments.Keys.Any(key => !string.Equals(key, "topic", StringComparison.Ordinal)))
        {
            return Deny(request.ToolName,
                "Only the optional topic argument is accepted.",
                EllaiToolReasonCodes.InvalidArguments);
        }

        var topic = request.Arguments.TryGetValue("topic", out var requestedTopic)
            ? requestedTopic
            : null;
        var facts = _productFacts.Find(topic);
        if (facts.Count == 0)
        {
            return Deny(request.ToolName,
                "No approved product fact matches that topic.",
                EllaiToolReasonCodes.InvalidArguments);
        }

        var evidence = facts.Select(fact => new EvidenceRef(
            "ProductFact",
            fact.FactId,
            fact.Revision,
            fact.SourceId,
            fact.FactId)).ToArray();
        var summary = string.Join("\n", facts.Select(fact =>
            $"[{fact.FactId}; source {fact.SourceId}; revision {fact.Revision}] {fact.Text}"));
        return new EllaiToolResult(
            request.ToolName,
            Allowed: true,
            DenialReason: null,
            Evidence: evidence,
            ResultSummary: summary,
            ItemCount: facts.Count,
            Truncated: false,
            ReasonCode: EllaiToolReasonCodes.Ok);
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
