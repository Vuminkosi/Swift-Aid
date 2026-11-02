using System.Text.Json;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Application.Intake;
using SwiftAid.Application.Intake.Imports;

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

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        State, Evidence, Topic, Capabilities, Receipts, ProductFacts, Now,
        DocsSearch, DocsGet, UiCatalogue, UiObserve, IngestionSchema, ValidateObservations, Locations,
        ImportDestinations, Imports, ImportStatus, ClassificationEvidence
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
    string ConsentPurpose = EllaiToolBroker.ToolReadPurpose);

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
        $"{EllaiToolNames.State}(projection=[feature|queue], limit=1..20, ref?) — permitted feature or live work queue.",
        $"{EllaiToolNames.Evidence}(ref, field?) — versioned source items with provenance.",
        $"{EllaiToolNames.Topic}(ref) — session topic candidates with checkpoints.",
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
        $"{EllaiToolNames.Locations}(q, country?, limit?) — search the public geographic registry; present matches for user selection, never guess a location.",
        $"{EllaiToolNames.ImportDestinations}() — current authorized organization/workspace/country destinations for Workspace imports; staff only.",
        $"{EllaiToolNames.Imports}(workspace_id, organization_id, country) — the current user's recent imports in one permitted destination; staff only.",
        $"{EllaiToolNames.ImportStatus}(import_id) — the current user's durable import state and saved/rejected/retry counts; no case contents and no submission.",
        $"{EllaiToolNames.ClassificationEvidence}(case_id) — persisted semantic classification evidence for an authorized current case revision; no inference, diagnosis or clinical probability.",
    };

    public static string DescribeCatalogue(IReadOnlySet<string>? allowedTools = null) =>
        string.Join("\n", ToolDescriptions.Where(description =>
        {
            if (allowedTools is null)
            {
                return true;
            }

            var separator = description.IndexOf('(');
            return separator > 0 && allowedTools.Contains(description[..separator]);
        }));

    private readonly EllaiPolicyService _policy;
    private readonly EllaiConsentService _consent;
    private readonly EllaiTechnicalDocs? _docs;
    private readonly ICaseQueueQuery? _queue;
    private readonly IEllaiApprovalStore? _receipts;
    private readonly EllaiTopicService? _topics;
    private readonly EllaiUiRegistry? _uiRegistry;
    private readonly IEllaiUiDiscoveryService? _uiDiscovery;
    private readonly IEllaiUiObservationStore? _uiObservations;
    private readonly EllaiProductFactCatalog _productFacts;
    private readonly IGeographicRegistry? _geography;
    private readonly ICaseImportStore? _imports;
    private readonly ICaseImportWorkspaceReader? _importWorkspaces;
    private readonly SwiftAid.Application.Classification.ICaseClassificationQuery? _classification;
    private readonly SwiftAid.Application.Surveillance.ISignalReviewService? _signals;

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
        SwiftAid.Application.Surveillance.ISignalReviewService? signals = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        _docs = docs;
        _queue = queue;
        _receipts = receipts;
        _topics = topics;
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

    public async Task<EllaiToolResult> InvokeAsync(
        EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
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
            return Bound(await ReadStateAsync(context, request, ct).ConfigureAwait(false));
        }

        if (string.Equals(canonical, EllaiToolNames.Receipts, StringComparison.Ordinal))
        {
            return Bound(await ReadReceiptsAsync(context, request, ct).ConfigureAwait(false));
        }

        if (string.Equals(canonical, EllaiToolNames.Topic, StringComparison.Ordinal))
        {
            return Bound(await ReadTopicsAsync(context, request, ct).ConfigureAwait(false));
        }

        if (string.Equals(canonical, EllaiToolNames.UiCatalogue, StringComparison.Ordinal))
        {
            return Bound(ReadUiCatalogue(context, request));
        }

        if (string.Equals(canonical, EllaiToolNames.UiObserve, StringComparison.Ordinal))
        {
            return Bound(await ReadUiObserveAsync(context, request, ct).ConfigureAwait(false));
        }

        if (canonical == EllaiToolNames.Locations)
            return Bound(await ReadLocationsAsync(request, ct).ConfigureAwait(false));

        if (canonical is EllaiToolNames.ImportDestinations or EllaiToolNames.Imports or EllaiToolNames.ImportStatus)
            return Bound(await ReadImportsAsync(context, request, ct).ConfigureAwait(false));
        if (canonical == EllaiToolNames.ClassificationEvidence)
            return Bound(await ReadClassificationAsync(context, request, ct).ConfigureAwait(false));

        return Bound(canonical switch
        {
            EllaiToolNames.Evidence => Allow(request.ToolName, "Authorized versioned source items.", Math.Min(request.Arguments.Count, MaxItems), request.Arguments.Count > MaxItems),
            EllaiToolNames.Capabilities => Allow(request.ToolName, "Current capability descriptors.", 1, false),
            EllaiToolNames.ProductFacts => ReadProductFacts(request),
            EllaiToolNames.Now => Allow(request.ToolName, DescribeNow(DateTimeOffset.UtcNow), 1, false),
            EllaiToolNames.DocsSearch => SearchDocs(request),
            EllaiToolNames.DocsGet => GetDocs(request),
            EllaiToolNames.IngestionSchema => ReadIngestionSchema(request),
            EllaiToolNames.ValidateObservations => ValidateObservations(request),
            _ => Deny(request.ToolName, "Unregistered tool.", EllaiToolReasonCodes.UnregisteredTool)
        });
    }

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
            [new EvidenceRef("ProductFact", "ingestion-schema", "1", "capture", "ingestion-schema/1")], summary, 1, false);
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

    private async Task<EllaiToolResult> ReadLocationsAsync(EllaiToolRequest request, CancellationToken ct)
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
        if (_geography is null) return Deny(request.ToolName, "Location search is unavailable.", EllaiToolReasonCodes.Unavailable);
        try
        {
            var matches = await _geography.SearchAsync(query, country, limit, ct).ConfigureAwait(false);
            var items = matches.Results.Take(limit).Select(item => new { item.Id, item.Name, item.CountryCode, item.Ancestors }).ToArray();
            return new(request.ToolName, true, null,
                items.Select(item => new EvidenceRef("GeographicRegistry", item.Id, matches.RegistryVersion, "name", item.Id)).ToArray(),
                JsonSerializer.Serialize(new { matches.RegistryVersion, matches = items, requiresUserSelection = true }),
                items.Length, matches.TotalCount > items.Length);
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

    /// <summary>Live <c>ellai_topic</c> resolution against the session's
    /// durable topic history: ordered mentions resolve first, multiple
    /// matches stay explicitly ambiguous, and unknown references are an
    /// authorized empty. Topics never leak across sessions.</summary>
    private async Task<EllaiToolResult> ReadTopicsAsync(
        EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
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

        var (resolved, ambiguous) = await _topics
            .ResolveAsync(context.SessionId, reference.Trim(), ct).ConfigureAwait(false);
        var matches = resolved.Take(MaxItems).ToList();
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

    // Public reporting is available without a workspace grant. A model-provided
    // feature may narrow this boundary, never expand it to private casework.
    private static string? ResolveUiFeature(EllaiToolContext context, EllaiToolRequest request)
    {
        var permitted = context.FeatureId == "public.product-help" ? EllaiReportUiManifest.FeatureId : context.FeatureId;
        return !request.Arguments.TryGetValue("feature", out var requested) || requested == permitted ? permitted : null;
    }

    /// <summary>Live <c>ellai_ui_catalogue</c> slice from the registered
    /// semantic registry: only descriptors the session feature is entitled
    /// to, bounded, without resolved URLs/selectors. Entitlement is the
    /// session feature plus an empty capability set — descriptors requiring
    /// capabilities stay excluded until a scope-capability source exists
    /// (fail-closed). An empty registry reads as an authorized empty.</summary>
    private EllaiToolResult ReadUiCatalogue(EllaiToolContext context, EllaiToolRequest request)
    {
        var feature = ResolveUiFeature(context, request);
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
            new HashSet<string>(StringComparer.Ordinal));
        var items = snapshot.Descriptors.Take(limit).ToList();
        if (items.Count == 0)
        {
            return Allow(request.ToolName, "No entitled controls are registered for this feature.", 0, false);
        }

        var lines = items.Select(d =>
            $"control {d.ControlId} · {d.ElementKind} · v{d.DescriptorVersion} · risk {d.RiskClass}" +
            (d.ObjectiveId is null ? string.Empty : $" · objective {d.ObjectiveId}") +
            $"; feature {d.FeatureId}; navigation {d.NavigationId}; purpose {d.Semantics?.Purpose}" +
            $"; human confirmation {d.HumanConfirmationRequired}");
        var evidence = items.Select(d => new EvidenceRef(
            "UiCatalogue", d.ControlId, d.DescriptorVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture), null, d.ControlId)).ToList();
        var summary = $"Semantic catalogue {snapshot.CatalogueVersion} " +
            $"contract {snapshot.UiContractVersion}: {items.Count} entitled control(s)" +
            (snapshot.Truncated ? " (truncated)." : ".") + "\n" + string.Join("\n", lines);
        return new EllaiToolResult(request.ToolName, true, null, evidence,
            summary, items.Count, snapshot.Truncated, EllaiToolReasonCodes.Ok);
    }

    /// <summary>Live <c>ellai_ui_observe</c> through the client-posted
    /// mounted-control channel. Ready only for entitled controls the latest
    /// session report shows mounted; everything else snapshot-entitled is
    /// not assumed. Unknown control references fail closed; a missing
    /// report is an authorized empty, never a fabricated view.</summary>
    private async Task<EllaiToolResult> ReadUiObserveAsync(
        EllaiToolContext context, EllaiToolRequest request, CancellationToken ct)
    {
        var feature = ResolveUiFeature(context, request);
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
        if (report is null)
        {
            return Allow(request.ToolName,
                "No client UI observation has been reported for this session.", 0, false);
        }

        if (view == "current") view = report.ViewInstanceId;

        var goalTags = request.Arguments.TryGetValue("context", out var goalContext)
            && !string.IsNullOrWhiteSpace(goalContext)
            ? goalContext.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList()
            : new List<string>();
        var snapshot = _uiRegistry.GetSnapshot(
            new UiRegistryQuery(feature, goalTags, MaxItems),
            new HashSet<string>(StringComparer.Ordinal) { feature },
            new HashSet<string>(StringComparer.Ordinal));
        var eligible = snapshot.Descriptors.ToDictionary(d => d.ControlId, StringComparer.Ordinal);
        var unknown = requested.Where(id => !eligible.ContainsKey(id)).ToList();
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
            $"control {o.ControlId} · {o.State} · view {o.ViewInstanceId} · {o.Platform}");
        var evidence = ordered.Select(o => new EvidenceRef(
            "UiObservation", o.ControlId, o.State.ToString(), null, o.ControlId)).ToList();
        var header = $"ClientObservation: {ordered.Count} control(s) from view " +
            $"{report.ViewInstanceId} ({report.ContextStamp}) reported " +
            $"{Math.Max(0, (long)age.TotalSeconds)}s ago on {report.Platform}." +
            (string.Equals(report.ViewInstanceId, view.Trim(), StringComparison.Ordinal)
                ? string.Empty
                : $" Requested view '{view.Trim()}' differs from the latest report.");
        return new EllaiToolResult(request.ToolName, true, null, evidence,
            header + "\n" + string.Join("\n", lines),
            ordered.Count, false, EllaiToolReasonCodes.Ok);
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
