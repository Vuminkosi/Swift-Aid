using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Builds the pinned policy and server context for an Ellai execution.</summary>
public sealed class EllaiExecutionPromptBuilder(EllaiPromptBuilder prompts, EllaiUsageLedger? ledger = null)
{
    internal const string FrameworkTurnProtocol =
        "Use the registered function tools for reads. Do not output tool calls as JSON text. " +
        "When finished, reply with exactly one JSON object: {\"final_decision\":{<EllaiDecision schema v2>}}. " +
        "Pure small talk answers directly through final_decision with no tool calls. " +
        "Clock readings in prior conversation are historical; use ellai_now for the current time.";
    private readonly EllaiPromptBuilder _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
    private readonly EllaiUsageLedger? _ledger = ledger;
    private static readonly JsonSerializerOptions ContractJson = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private static readonly string DecisionSchema = JsonSchemaExporter
        .GetJsonSchemaAsNode(ContractJson, typeof(EllaiDecision)).ToJsonString();

    public string Build(EllaiLoopRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limits = $"cycle {request.CycleId}" +
            (_ledger is null || !_ledger.EnforcesLimits
                ? string.Empty
                : $", {Math.Max(0, _ledger.RemainingFor(request.Context.SessionId))} session tokens left");
        var system = _prompts.Build(request.Role, request.CapabilitySummary, limits) +
            "\n## Current context\nServer time (UTC): " + DateTimeOffset.UtcNow.ToString("O") +
            ". Client-reported page: " + (EllaiPageContext.DisplayName(request.ActivePage) ?? "Unknown") +
            ". Page context is advisory; it grants no record access or tool authority. Do not infer a local time zone." +
            "\n" + EllaiInteractionContext.Describe(request.InteractionMode) +
            "\nStored guidance state: " + (request.GuidanceState is "Ready" or "Paused" or "Cancelled" or "Completed" or "Generating" or "Failed"
                ? request.GuidanceState : "Unavailable") + ". This is server state, not proof that an overlay is visible." +
            "\n## Turn protocol\n" + FrameworkTurnProtocol +
            "\n## Final decision JSON schema\n" + DecisionSchema +
            "\nThe schema describes data shape, not permission. Claims must cite exact evidence objects returned by permitted tools. " +
            "Do not invent evidence references, UI bindings or action authority. Use empty arrays/null for nonapplicable fields." +
            "\n## Tool catalogue\n" + EllaiToolBroker.DescribeCatalogue(request.Context.AllowedTools);
        system += "\n## Aggregate questions\nNever infer a case count from a partial list, prior conversation, or model knowledge. " +
            "For a current count of the user's own readable managed cases, use ellai_owned_case_count with the requested time window when available. " +
            "If the window is unclear, ask for it. If that tool is unavailable or the requested aggregate is outside its scope, explain the limitation without inventing a number. " +
            "The count covers cases, not animals, diagnoses, incidence, or a whole population.";
        if (request.Context.AllowedTools is null || request.Context.AllowedTools.Contains(EllaiToolNames.UiCatalogue))
        {
            system += "\n## Interactive guidance\nFor requests to show, explain, or guide through application controls, " +
                "discover the relevant feature using ellai_ui_catalogue before proposing guidance. " +
                "For an application overview or system tour, query feature=navigation; for public case reporting, query feature=report. For dashboard controls and filters, query feature=dashboard; for case-list controls and filters, query feature=case-list; for the currently open case-detail page, query feature=case-detail. For public community-session browsing, query feature=community-sessions. For map controls and layers, query feature=surveillance-map. For investigation evidence, proposals and review controls, query feature=investigations. For assigned or open follow-ups and escalations, query feature=monitoring; these are work queues, not outbreak signals. For organization membership, creation and invitation controls, query feature=organizations. Never ask for or repeat invitation codes; these controls remain human-operated. A case-detail panel observation does not prove record access or data loading. " +
                "Discovery enforces current permissions; a denied navigation catalogue does not grant access through another tool. " +
                "Use ellai_ui_observe with view=current to inspect the latest client-reported state; unavailable controls are not proof of unavailable routes. " +
                "For a discovered supported feature, propose a StartGuidance UI intent with featureId=ellai.guidance, " +
                "resourceId=null, controlId=null and arguments containing only feature (the discovered feature ID) and objective (the user's goal). " +
                "The application validates and prepares the plan. Do not invent routes, anchors, completed actions or say the tour has started before application confirmation. " +
                "An explicit tour/demo/guidance request already authorizes starting read-only guidance. Emit the intent on that turn; " +
                "do not ask Ready?, Want me to start?, or demand another confirmation. A yes/ready/let's go reply accepts the existing request. " +
                "When stored guidance is Ready or Paused and the user asks to start that same tour, use ControlGuidance command=resume " +
                "to restore the existing plan instead of repeatedly proposing a new tour. If Generating, explain that preparation is in progress. " +
                "This does not authorize form submission, tool approvals or consequential operations. " +
                "If discovery returns no eligible feature, explain the limitation without pretending to navigate. " +
                "For an explicit request to pause, resume or cancel existing guidance, propose ControlGuidance with featureId=ellai.guidance, " +
                "resourceId=null, controlId=null and exactly one argument command=pause, resume or cancel. " +
                "For stop speaking while keeping guidance, use command=stop-speech; never cancel the tour for a speech-only interruption. " +
                "For an explicit request to skip a guidance step, use command=skip. The Gateway permits only optional explanation; skipping never proves an input, approval or submission. For an explicit request to revisit the previous guidance step, use command=back. This revisits guidance, never undoes application actions. For an explicit request to advance the current guidance step, use command=next. This requests evidence-checked advancement, " +
                "never completion of a field, submission, or approval. Do not claim advancement until application evidence confirms it. " +
                "For an explicit request to open an application area, discover navigation and propose NavigateFeature with featureId=navigation, " +
                "resourceId=null, controlId=the discovered navigation control and empty arguments. Never supply a URL. " +
                "For return to the previous application page use NavigateFeature, featureId=navigation, resourceId=null, controlId=null and command=back as the only argument. " +
                "Back uses available local application history and rechecks access; it never undoes a business operation or approval. " +
                "For an explicit request to filter the currently displayed case list, discover case-list and propose SetPermittedFilter with featureId=case-list, " +
                "resourceId=null, controlId=the available case-list filter ID and exactly one value argument. Use a current option key or the user's exact option label; " +
                "never invent option IDs. Empty value clears that one filter. Date values use yyyy-MM-dd UTC; sort uses newest or oldest. " +
                "To clear all case filters use controlId=case-list.clear and empty arguments. The component validates current options and the server rechecks data access. " +
                "Propose only one filter/navigation/guidance operation per turn; do not claim filtered results before the application query returns. " +
                "For an explicit request to open a case, use the exact case ID from authorized tool results or the user's supplied case ID; never invent one. " +
                "Propose OpenAuthorizedRecord with featureId=case-detail, resourceId=the canonical case GUID, controlId=null and empty arguments. " +
                "The application rechecks case access before navigation. Do not claim it opened until application confirmation. Do not combine it with other navigation or guidance controls. " +
                "For an explicit request to select a tab in the currently open case detail, discover case-detail first, then propose SelectTab " +
                "with featureId=case-detail, controlId=case-detail.tabs, resourceId=null and exactly one argument tab=" +
                "overview, history, follow-ups, assistance, discussion, attachments or observations. This does not open another record, " +
                "authorize a write or prove data loading. Do not combine tab selection with guidance controls. " +
                "Emit at most one guidance intent per turn. These are requested controls, not evidence that the client executed them.";
        }
        return system;
    }
}
