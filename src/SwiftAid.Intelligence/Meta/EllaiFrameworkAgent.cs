using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>A bounded single-agent execution building block. Gateway integration
/// must supply trusted instructions, durable history, usage and lifecycle fencing.</summary>
public sealed class EllaiFrameworkAgent
{
    private const int MaxResponseCharacters = 512_000;
    private readonly IChatClient _client;
    private readonly IEllaiFrameworkAgentFactory _agentFactory;

    public EllaiFrameworkAgent(IChatClient client, IEllaiFrameworkAgentFactory? agentFactory = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _agentFactory = agentFactory ?? EllaiFrameworkAgentFactory.Instance;
    }
    public int LastModelCallCount { get; private set; }
    public string? LastFailureCode { get; private set; }
    public bool LastRepairUsed { get; private set; }
    public string? LastFrameworkSessionJson { get; private set; }

    public async Task<EllaiDecision?> RunAsync(
        EllaiToolContext context, string instructions, IReadOnlyList<ChatMessage> messages, int maxOutputTokens,
        Func<CancellationToken, Task<bool>> isCurrent,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke,
        EllaiFrameworkUsage usage,
        CancellationToken ct, Func<string, CancellationToken, ValueTask>? onTextDelta = null,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>>? revalidateTool = null,
        Func<string, CancellationToken, ValueTask>? onVerifiedStreamSegment = null,
        Func<CancellationToken, Task<bool>>? revalidateStreamObservations = null,
        string? frameworkSessionJson = null, int maxModelCalls = int.MaxValue,
        bool enableKnowledgeSpecialist = false, bool enableCaseSpecialist = false, bool enableAnalyticsSpecialist = false,
        bool allowEmptyCaseQueue = false, bool enableUiSpecialist = false, bool enableGeospatialSpecialist = false,
        bool enableWorkflowSpecialist = false, bool enableCommunitySessionsSpecialist = false, bool enableSurveillanceSpecialist = false,
        bool enableReportingSpecialist = false, bool enableSpatialSpecialist = false, bool enableHealthResearchSpecialist = false,
        bool enableOperationalReadSpecialists = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(invoke);
        ArgumentNullException.ThrowIfNull(usage);
        if (!string.Equals(context.SessionId, usage.SessionId, StringComparison.Ordinal))
            throw new ArgumentException("Usage must belong to the current session.", nameof(usage));
        if (messages.Count == 0 || maxOutputTokens is < 1 or > 16384 || maxModelCalls < 1)
            throw new ArgumentException("Messages and a bounded token limit are required.");
        LastFailureCode = null;
        LastFrameworkSessionJson = null;
        LastRepairUsed = false;
        LastModelCallCount = 0;
        var invalidDelegation = 0;
        async Task<bool> Current(CancellationToken token)
        {
            try
            {
                var allowed = Volatile.Read(ref invalidDelegation) == 0 && await isCurrent(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return allowed;
            }
            catch (Exception error) when (token.IsCancellationRequested && error is not OperationCanceledException and not OutOfMemoryException)
            {
                throw new OperationCanceledException(token);
            }
        }
        if (context.RemainingCalls <= 0) { LastFailureCode = EllaiToolReasonCodes.BudgetExceeded; return null; }
        var initialCurrent = await Current(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!initialCurrent) { LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy; return null; }

        var evidence = new HashSet<EvidenceRef>();
        var evidenceSources = new Dictionary<EvidenceRef, List<string>>();
        EllaiToolResult? countResult = null;
        EllaiToolRequest? countRequest = null;
        EllaiToolResult? spatialResult = null;
        EllaiToolRequest? spatialRequest = null;
        EllaiToolResult? healthSourceResult = null;
        EllaiToolRequest? healthSourceRequest = null;
        EllaiToolResult? emptyQueueResult = null;
        EllaiToolRequest? emptyQueueRequest = null;
        var toolCalls = 0;
        var streamSafeToolObservations = true;
        var guard = new EllaiFrameworkToolGuard(Math.Min(context.RemainingCalls, EllaiExecutionLimits.MaxToolCalls),
            Current, async (toolContext, request, token) =>
            {
                var result = await invoke(toolContext, request, token).ConfigureAwait(false);
                toolCalls++;
                // These brokered observations carry no private evidence and
                // already have explicit safe replay rules in conversation history.
                var publicObservation = request.ToolName is EllaiToolNames.Now or
                    EllaiToolNames.Capabilities or EllaiToolNames.ValidateObservations;
                var emptyCaseRecall = request.ToolName == EllaiToolNames.Topic &&
                    request.Arguments.TryGetValue("ref", out var recallReference) &&
                    string.Equals(recallReference, EllaiCaseResultMemory.Reference, StringComparison.OrdinalIgnoreCase) &&
                    result.HistoryValidUntilUtc > DateTimeOffset.UtcNow && result.ItemCount == 0 &&
                    (result.ResultSummary.TrimEnd() == "The last retained case result set was empty." ||
                     result.ResultSummary.TrimEnd() == "Previously retrieved cases, checked against current access; this is not a new search. Unavailable records are omitted.");
                streamSafeToolObservations &= (publicObservation || emptyCaseRecall) &&
                    result.Allowed && result.ToolName == request.ToolName && !result.Truncated &&
                    result.ReasonCode == EllaiToolReasonCodes.Ok && result.Evidence.Count == 0;
                if (request.ToolName == EllaiToolNames.OwnedCaseCount && result.Allowed)
                {
                    countResult = result;
                    countRequest = request;
                }
                if (request.ToolName == EllaiToolNames.OwnedRegionCounts && result.Allowed)
                {
                    spatialResult = result;
                    spatialRequest = request;
                }
                if (request.ToolName == EllaiToolNames.ApprovedHealthSources && result.Allowed)
                {
                    healthSourceResult = result;
                    healthSourceRequest = request;
                }
                if ((enableCaseSpecialist || allowEmptyCaseQueue) && IsEmptyQueueObservation(request, result))
                {
                    emptyQueueResult = result;
                    emptyQueueRequest = request;
                }
                if (result.Allowed)
                    foreach (var reference in result.Evidence.Take(20))
                    {
                        evidence.Add(reference);
                        if (!evidenceSources.TryGetValue(reference, out var sources))
                            evidenceSources[reference] = sources = [];
                        sources.AddRange(EvidenceLines(request.ToolName, result, reference));
                    }
                return result;
            });
        async Task<EllaiToolResult> InvokeGuardedTool(EllaiToolContext toolContext, EllaiToolRequest request, CancellationToken token)
        {
            var result = await guard.InvokeAsync(toolContext, request, token).ConfigureAwait(false);
            // Only stop after the guard's post-call authorization has passed.
            // The final count is constructed from a fresh authorized read below.
            if (((request.ToolName == EllaiToolNames.OwnedCaseCount || request.ToolName == EllaiToolNames.OwnedRegionCounts || request.ToolName == EllaiToolNames.ApprovedHealthSources) && result.Allowed ||
                 (enableCaseSpecialist || allowEmptyCaseQueue) && IsEmptyQueueObservation(request, result)) && toolCalls == 1 &&
                FunctionInvokingChatClient.CurrentContext is { FunctionCount: 1 } invocation)
                invocation.Terminate = true;
            return result;
        }
        var tools = EllaiFrameworkToolCatalog.Create(context, InvokeGuardedTool).ToList();
        var delegatedModelCalls = 0;
        if (enableKnowledgeSpecialist && context.AllowedTools is { } parentTools &&
            (parentTools.Contains(EllaiToolNames.DocsSearch) || parentTools.Contains(EllaiToolNames.DocsGet)))
        {
            var delegationCount = 0;
            async Task<string> DelegateKnowledge(string task, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref delegationCount) != 1)
                    return "Knowledge delegation limit reached.";
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.TryGet(
                    SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.Knowledge, out var profile) ? profile.TimeoutSeconds : 1));
                try
                {
                    var result = await new EllaiKnowledgeSpecialist(_client, _agentFactory).RunAsync(task,
                        instructions, true, context, usage, Current, InvokeGuardedTool, deadline.Token,
                        calls => Interlocked.Add(ref delegatedModelCalls, calls), revalidateTool ?? invoke).ConfigureAwait(false);
                    if (result.Outcome == EllaiSpecialistOutcome.Stale)
                    {
                        Interlocked.Exchange(ref invalidDelegation, 1);
                        return "Documentation changed while it was being checked.";
                    }
                    if (result.Outcome != EllaiSpecialistOutcome.Completed || result.Decision is null)
                    {
                        Interlocked.Exchange(ref invalidDelegation, 1);
                        return "Knowledge specialist could not verify an answer.";
                    }
                    var json = System.Text.Json.JsonSerializer.Serialize(result.Decision);
                    if (json.Length <= 12000) return json;
                    Interlocked.Exchange(ref invalidDelegation, 1);
                    return "Knowledge specialist result exceeded its limit.";
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Knowledge specialist timed out."; }
                catch (EllaiFrameworkFailureException)
                {
                    Interlocked.Exchange(ref invalidDelegation, 1);
                    return "Knowledge specialist could not complete the request.";
                }
            }
            tools.Add(AIFunctionFactory.Create(DelegateKnowledge, name: "ellai_delegate_knowledge",
                description: "Delegate a question about approved documentation or other admitted SwiftAid information to the knowledge specialist."));
        }
        if (enableCaseSpecialist && context.Actor is not null &&
            (context.AllowedTools?.Contains(EllaiToolNames.CaseTimeline) == true ||
             context.AllowedTools?.Contains(EllaiToolNames.State) == true))
        {
            var delegationCount = 0;
            async Task<string> DelegateCase(string task, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref delegationCount) != 1)
                    return "Case delegation limit reached.";
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.TryGet(
                    SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.Case, out var profile) ? profile.TimeoutSeconds : 1));
                try
                {
                    var result = await new EllaiCaseSpecialist(_client, _agentFactory).RunAsync(task,
                        instructions, true, context, usage, Current, InvokeGuardedTool, deadline.Token,
                        calls => Interlocked.Add(ref delegatedModelCalls, calls), revalidateTool ?? invoke).ConfigureAwait(false);
                    if (result.Outcome == EllaiSpecialistOutcome.Stale)
                    {
                        Interlocked.Exchange(ref invalidDelegation, 1);
                        return "Case data changed while it was being checked.";
                    }
                    if (result.Outcome != EllaiSpecialistOutcome.Completed || result.Decision is null)
                    {
                        Interlocked.Exchange(ref invalidDelegation, 1);
                        return "Case specialist could not verify an answer.";
                    }
                    var json = System.Text.Json.JsonSerializer.Serialize(result.Decision);
                    if (json.Length <= 12000) return json;
                    Interlocked.Exchange(ref invalidDelegation, 1);
                    return "Case specialist result exceeded its limit.";
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Case specialist timed out."; }
                catch (EllaiFrameworkFailureException error) when (error.Code != EllaiToolReasonCodes.DeniedByPolicy)
                {
                    Interlocked.Exchange(ref invalidDelegation, 1);
                    return "Case specialist could not complete the request.";
                }
            }
            tools.Add(AIFunctionFactory.Create(DelegateCase, name: "ellai_delegate_case",
                description: "Delegate one bounded question about the authorized owned-case queue, a case timeline, or possible related-case suggestions and their review status. Suggestions do not establish identity or causation."));
        }
        if (enableAnalyticsSpecialist && context.Actor is not null &&
            context.AllowedTools?.Contains(EllaiToolNames.OwnedCaseCount) == true)
        {
            var delegationCount = 0;
            async Task<string> DelegateAnalytics(string task, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref delegationCount) != 1)
                    return "Analytics delegation limit reached.";
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.TryGet(
                    SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.Analytics, out var profile) ? profile.TimeoutSeconds : 1));
                try
                {
                    var result = await new EllaiAnalyticsSpecialist(_client, _agentFactory).RunAsync(task,
                        instructions, true, context, usage, Current, InvokeGuardedTool, revalidateTool ?? invoke, deadline.Token,
                        calls => Interlocked.Add(ref delegatedModelCalls, calls)).ConfigureAwait(false);
                    if (result.Outcome != EllaiSpecialistOutcome.Completed || result.Decision is null)
                    {
                        Interlocked.Exchange(ref invalidDelegation, 1);
                        return "Analytics specialist could not verify an answer.";
                    }
                    var json = System.Text.Json.JsonSerializer.Serialize(result.Decision);
                    if (json.Length <= 12000) return json;
                    Interlocked.Exchange(ref invalidDelegation, 1);
                    return "Analytics specialist result exceeded its limit.";
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Analytics specialist timed out."; }
                catch (EllaiFrameworkFailureException)
                {
                    Interlocked.Exchange(ref invalidDelegation, 1);
                    return "Analytics specialist could not complete the request.";
                }
            }
            tools.Add(AIFunctionFactory.Create(DelegateAnalytics, name: "ellai_delegate_analytics",
                description: "Delegate an authorized owned-case count with an explicit 1 to 365 day window, or another admitted SwiftAid read."));
        }
        async Task<string> DelegateMvpRead(string agentId, string task, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.TryGet(agentId, out var profile)
                ? profile.TimeoutSeconds : 1));
            try
            {
                var result = await new EllaiMvpReadSpecialist(_client, _agentFactory).RunAsync(agentId, task, instructions,
                    context, usage, Current, InvokeGuardedTool, revalidateTool ?? invoke, deadline.Token,
                    calls => Interlocked.Add(ref delegatedModelCalls, calls)).ConfigureAwait(false);
                if (result.Outcome != EllaiSpecialistOutcome.Completed || result.Decision is null)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Specialist could not verify an answer."; }
                var json = System.Text.Json.JsonSerializer.Serialize(result.Decision);
                if (json.Length <= 12000) return json;
                Interlocked.Exchange(ref invalidDelegation, 1);
                return "Specialist result exceeded its limit.";
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
            { Interlocked.Exchange(ref invalidDelegation, 1); return "Specialist timed out."; }
            catch (EllaiFrameworkFailureException)
            { Interlocked.Exchange(ref invalidDelegation, 1); return "Specialist could not complete the request."; }
        }
        if (enableOperationalReadSpecialists && context.Actor is not null &&
            context.AllowedTools?.Contains(EllaiToolNames.CaseTimeline) == true)
        {
            var operationalRoles = new[]
            {
                (SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.Enrichment, "ellai_delegate_enrichment_read", "enrichment"),
                (SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.Organization, "ellai_delegate_organization_read", "organization"),
                (SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.FollowUp, "ellai_delegate_followup_read", "follow-up"),
                (SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.Quality, "ellai_delegate_quality_read", "data quality"),
                (SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.Correlation, "ellai_delegate_correlation_read", "correlation")
            };
            foreach (var (agentId, functionName, roleName) in operationalRoles)
            {
                var delegationCount = 0;
                Task<string> DelegateOperationalRead(string task, CancellationToken token) => Interlocked.Increment(ref delegationCount) == 1
                    ? DelegateMvpRead(agentId, task, token)
                    : Task.FromResult("Operational specialist read delegation limit reached.");
                tools.Add(AIFunctionFactory.Create(DelegateOperationalRead, name: functionName,
                    description: $"Ask the {roleName} specialist a SwiftAid question using the session's admitted reads. This does not run a background job or execute an action."));
            }
        }
        if (enableHealthResearchSpecialist && context.Actor is not null && context.AllowedTools?.Contains(EllaiToolNames.ApprovedHealthSources) == true)
        {
            var delegationCount = 0;
            async Task<string> DelegateHealthResearch(string topic, string jurisdiction, string language, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref delegationCount) != 1) return "Health-source delegation limit reached.";
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.TryGet(
                    SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.HealthResearch, out var profile) ? profile.TimeoutSeconds : 1));
                try
                {
                    var result = await new EllaiHealthResearchSpecialist().RunAsync(topic, jurisdiction, language, true, context, usage,
                        Current, InvokeGuardedTool, revalidateTool ?? invoke, deadline.Token).ConfigureAwait(false);
                    if (result.Outcome != EllaiSpecialistOutcome.Completed || result.Decision is null)
                    { Interlocked.Exchange(ref invalidDelegation, 1); return "Approved source references could not be verified."; }
                    var json = System.Text.Json.JsonSerializer.Serialize(result.Decision);
                    if (json.Length <= 12000) return json;
                    Interlocked.Exchange(ref invalidDelegation, 1);
                    return "Approved source references exceeded their limit.";
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Approved source search timed out."; }
                catch (EllaiFrameworkFailureException)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Approved source references could not be retrieved."; }
            }
            tools.Add(AIFunctionFactory.Create(DelegateHealthResearch, name: "ellai_delegate_health_research",
                description: "List approved source references for an exact topic, jurisdiction, and language. Do not generate advice or health education."));
            var generalDelegations = 0;
            Task<string> DelegateHealthRead(string task, CancellationToken token) => Interlocked.Increment(ref generalDelegations) == 1
                ? DelegateMvpRead(SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.HealthResearch, task, token)
                : Task.FromResult("Health-research read delegation limit reached.");
            tools.Add(AIFunctionFactory.Create(DelegateHealthRead, name: "ellai_delegate_health_research_read",
                description: "Ask the health-research specialist a SwiftAid question using the session's admitted reads."));
        }
        if (enableSpatialSpecialist && context.Actor is not null && context.AllowedTools?.Contains(EllaiToolNames.OwnedRegionCounts) == true)
        {
            var delegationCount = 0;
            async Task<string> DelegateSpatial(string task, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref delegationCount) != 1) return "Spatial delegation limit reached.";
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.TryGet(
                    SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.Spatial, out var profile) ? profile.TimeoutSeconds : 1));
                try
                {
                    var result = await new EllaiSpatialSpecialist().RunAsync(task, true, context, usage, Current,
                        InvokeGuardedTool, revalidateTool ?? invoke, deadline.Token).ConfigureAwait(false);
                    if (result.Outcome != EllaiSpecialistOutcome.Completed || result.Decision is null)
                    { Interlocked.Exchange(ref invalidDelegation, 1); return "Spatial analysis could not verify an answer."; }
                    var json = System.Text.Json.JsonSerializer.Serialize(result.Decision);
                    if (json.Length <= 12000) return json;
                    Interlocked.Exchange(ref invalidDelegation, 1);
                    return "Spatial result exceeded its limit.";
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Spatial analysis timed out."; }
                catch (EllaiFrameworkFailureException)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Spatial analysis could not complete the request."; }
            }
            tools.Add(AIFunctionFactory.Create(DelegateSpatial, name: "ellai_delegate_spatial",
                description: "Delegate a bounded count of the current officer's readable cases by region over the last 30 days; not a hotspot or incidence estimate."));
            var generalDelegations = 0;
            Task<string> DelegateSpatialRead(string task, CancellationToken token) => Interlocked.Increment(ref generalDelegations) == 1
                ? DelegateMvpRead(SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.Spatial, task, token)
                : Task.FromResult("Spatial read delegation limit reached.");
            tools.Add(AIFunctionFactory.Create(DelegateSpatialRead, name: "ellai_delegate_spatial_read",
                description: "Ask the spatial specialist a SwiftAid question using the session's admitted reads."));
        }
        if (enableUiSpecialist && context.AllowedTools is { } uiTools &&
            (uiTools.Contains(EllaiToolNames.UiCatalogue) || uiTools.Contains(EllaiToolNames.UiObserve)))
        {
            var delegationCount = 0;
            async Task<string> DelegateUi(string task, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref delegationCount) != 1) return "UI delegation limit reached.";
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.TryGet(
                    SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.Ui, out var profile) ? profile.TimeoutSeconds : 1));
                try
                {
                    var result = await new EllaiUiSpecialist(_client, _agentFactory).RunAsync(task, instructions, context,
                        usage, Current, InvokeGuardedTool, revalidateTool ?? invoke, deadline.Token,
                        calls => Interlocked.Add(ref delegatedModelCalls, calls)).ConfigureAwait(false);
                    if (result.Outcome == EllaiSpecialistOutcome.Stale)
                    {
                        Interlocked.Exchange(ref invalidDelegation, 1);
                        return "The UI changed while it was being checked.";
                    }
                    if (result.Outcome != EllaiSpecialistOutcome.Completed || result.Decision is null)
                    {
                        Interlocked.Exchange(ref invalidDelegation, 1);
                        return "UI specialist could not verify an answer.";
                    }
                    var json = System.Text.Json.JsonSerializer.Serialize(result.Decision);
                    if (json.Length <= 12000) return json;
                    Interlocked.Exchange(ref invalidDelegation, 1);
                    return "UI specialist result exceeded its limit.";
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "UI specialist timed out."; }
                catch (EllaiFrameworkFailureException)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "UI specialist could not complete the request."; }
            }
            tools.Add(AIFunctionFactory.Create(DelegateUi, name: "ellai_delegate_ui",
                description: "Delegate one read-only question about entitled controls and the current screen."));
        }
        if (enableGeospatialSpecialist && context.AllowedTools?.Contains(EllaiToolNames.Locations) == true)
        {
            var delegationCount = 0;
            async Task<string> DelegateGeospatial(string task, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref delegationCount) != 1) return "Geographic delegation limit reached.";
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.TryGet(
                    SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.Geospatial, out var profile) ? profile.TimeoutSeconds : 1));
                try
                {
                    var result = await new EllaiGeospatialSpecialist(_client, _agentFactory).RunAsync(task, instructions, context,
                        usage, Current, InvokeGuardedTool, revalidateTool ?? invoke, deadline.Token,
                        calls => Interlocked.Add(ref delegatedModelCalls, calls)).ConfigureAwait(false);
                    if (result.Outcome != EllaiSpecialistOutcome.Completed || result.Decision is null)
                    {
                        Interlocked.Exchange(ref invalidDelegation, 1);
                        return "Geographic specialist could not verify an answer.";
                    }
                    var json = System.Text.Json.JsonSerializer.Serialize(result.Decision);
                    if (json.Length <= 12000) return json;
                    Interlocked.Exchange(ref invalidDelegation, 1);
                    return "Geographic specialist result exceeded its limit.";
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Geographic specialist timed out."; }
                catch (EllaiFrameworkFailureException)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Geographic specialist could not complete the request."; }
            }
            tools.Add(AIFunctionFactory.Create(DelegateGeospatial, name: "ellai_delegate_geospatial",
                description: "Find registry-backed place candidates or read other admitted SwiftAid information; does not perform case mapping or spatial analysis."));
        }
        if (enableWorkflowSpecialist && context.AllowedTools?.Contains(EllaiToolNames.Receipts) == true)
        {
            var delegationCount = 0;
            async Task<string> DelegateWorkflow(string task, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref delegationCount) != 1) return "Workflow delegation limit reached.";
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.TryGet(
                    SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.Workflow, out var profile) ? profile.TimeoutSeconds : 1));
                try
                {
                    var result = await new EllaiWorkflowSpecialist(_client, _agentFactory).RunAsync(task, instructions, context,
                        usage, Current, InvokeGuardedTool, revalidateTool ?? invoke, deadline.Token,
                        calls => Interlocked.Add(ref delegatedModelCalls, calls)).ConfigureAwait(false);
                    if (result.Outcome != EllaiSpecialistOutcome.Completed || result.Decision is null)
                    {
                        Interlocked.Exchange(ref invalidDelegation, 1);
                        return "Workflow specialist could not verify an answer.";
                    }
                    var json = System.Text.Json.JsonSerializer.Serialize(result.Decision);
                    if (json.Length <= 12000) return json;
                    Interlocked.Exchange(ref invalidDelegation, 1);
                    return "Workflow specialist result exceeded its limit.";
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Workflow specialist timed out."; }
                catch (EllaiFrameworkFailureException)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Workflow specialist could not complete the request."; }
            }
            tools.Add(AIFunctionFactory.Create(DelegateWorkflow, name: "ellai_delegate_workflow",
                description: "Read current-session action receipt status or other admitted SwiftAid information; cannot approve or execute actions."));
        }
        if (enableCommunitySessionsSpecialist && context.AllowedTools?.Contains(EllaiToolNames.PublicSessions) == true)
        {
            var delegationCount = 0;
            async Task<string> DelegateCommunitySessions(string task, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref delegationCount) != 1) return "Community sessions delegation limit reached.";
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.TryGet(
                    SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.CommunitySessions, out var profile) ? profile.TimeoutSeconds : 1));
                try
                {
                    var result = await new EllaiCommunitySessionsSpecialist(_client, _agentFactory).RunAsync(task, instructions, context,
                        usage, Current, InvokeGuardedTool, revalidateTool ?? invoke, deadline.Token,
                        calls => Interlocked.Add(ref delegatedModelCalls, calls)).ConfigureAwait(false);
                    if (result.Outcome != EllaiSpecialistOutcome.Completed || result.Decision is null)
                    {
                        Interlocked.Exchange(ref invalidDelegation, 1);
                        return "Community sessions specialist could not verify an answer.";
                    }
                    var json = System.Text.Json.JsonSerializer.Serialize(result.Decision);
                    if (json.Length <= 12000) return json;
                    Interlocked.Exchange(ref invalidDelegation, 1);
                    return "Community sessions specialist result exceeded its limit.";
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Community sessions specialist timed out."; }
                catch (EllaiFrameworkFailureException)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Community sessions specialist could not complete the request."; }
            }
            tools.Add(AIFunctionFactory.Create(DelegateCommunitySessions, name: "ellai_delegate_community_sessions",
                description: "Read published public sessions or other admitted SwiftAid information; cannot schedule or execute actions."));
        }
        if (enableSurveillanceSpecialist && context.AllowedTools is { } surveillanceTools &&
            (surveillanceTools.Contains(EllaiToolNames.ClassificationSnapshot) || surveillanceTools.Contains(EllaiToolNames.SurveillanceSignals)))
        {
            var delegationCount = 0;
            async Task<string> DelegateSurveillance(string task, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref delegationCount) != 1) return "Surveillance delegation limit reached.";
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.TryGet(
                    SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.Surveillance, out var profile) ? profile.TimeoutSeconds : 1));
                try
                {
                    var result = await new EllaiSurveillanceSpecialist(_client, _agentFactory).RunAsync(task, instructions, context,
                        usage, Current, InvokeGuardedTool, revalidateTool ?? invoke, deadline.Token,
                        calls => Interlocked.Add(ref delegatedModelCalls, calls)).ConfigureAwait(false);
                    if (result.Outcome != EllaiSpecialistOutcome.Completed || result.Decision is null)
                    {
                        Interlocked.Exchange(ref invalidDelegation, 1);
                        return "Surveillance specialist could not verify an answer.";
                    }
                    var json = System.Text.Json.JsonSerializer.Serialize(result.Decision);
                    if (json.Length <= 12000) return json;
                    Interlocked.Exchange(ref invalidDelegation, 1);
                    return "Surveillance specialist result exceeded its limit.";
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Surveillance specialist timed out."; }
                catch (EllaiFrameworkFailureException)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Surveillance specialist could not complete the request."; }
            }
            tools.Add(AIFunctionFactory.Create(DelegateSurveillance, name: "ellai_delegate_surveillance",
                description: "Read authorized persisted classification, review signals or other admitted SwiftAid information, without making a diagnosis."));
        }
        if (enableReportingSpecialist && context.AllowedTools is { } reportingTools &&
            (reportingTools.Contains(EllaiToolNames.UiCatalogue) || reportingTools.Contains(EllaiToolNames.UiObserve)))
        {
            var delegationCount = 0;
            async Task<string> DelegateReporting(string task, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref delegationCount) != 1) return "Reporting delegation limit reached.";
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.TryGet(
                    SwiftAid.Application.Ellai.Specialists.EllaiSpecialistRegistry.Reporting, out var profile) ? profile.TimeoutSeconds : 1));
                try
                {
                    var result = await new EllaiReportingSpecialist(_client, _agentFactory).RunAsync(task, instructions, context,
                        usage, Current, InvokeGuardedTool, revalidateTool ?? invoke, deadline.Token,
                        calls => Interlocked.Add(ref delegatedModelCalls, calls)).ConfigureAwait(false);
                    if (result.Outcome == EllaiSpecialistOutcome.Stale)
                    {
                        Interlocked.Exchange(ref invalidDelegation, 1);
                        return "The UI changed while it was being checked.";
                    }
                    if (result.Outcome != EllaiSpecialistOutcome.Completed || result.Decision is null)
                    {
                        Interlocked.Exchange(ref invalidDelegation, 1);
                        return "Reporting specialist could not verify an answer.";
                    }
                    var json = System.Text.Json.JsonSerializer.Serialize(result.Decision);
                    if (json.Length <= 12000) return json;
                    Interlocked.Exchange(ref invalidDelegation, 1);
                    return "Reporting specialist result exceeded its limit.";
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Reporting specialist timed out."; }
                catch (EllaiFrameworkFailureException)
                { Interlocked.Exchange(ref invalidDelegation, 1); return "Reporting specialist could not complete the request."; }
            }
            tools.Add(AIFunctionFactory.Create(DelegateReporting, name: "ellai_delegate_reporting",
                description: "Read entitled report-form controls and current observation; cannot submit or modify a report."));
        }
        var guardedClient = new ModelCallGuard(_client, Current, usage, maxModelCalls);
        var functionClient = new FunctionInvokingChatClient(guardedClient)
        {
            MaximumIterationsPerRequest = EllaiExecutionLimits.MaxIterations
        };
        if (onVerifiedStreamSegment is not null)
            instructions += "\nFor a plain Answer with no UI action or question, put stream_segments first in the final JSON envelope. " +
                "Each entry is an object with text and claims, where claims use the same GroundedClaim schema and exact evidence as final_decision. " +
                "Each text must exactly match a complete line bound to each cited reference, " +
                "or the complete product-fact text after that fact's source prefix. " +
                "Each segment must have a nonempty claims array; each claim text must equal the segment text and cite observed evidence. " +
                "If no evidence-backed segment is available, omit stream_segments and return the final_decision normally. " +
                "The final_decision displayText and spokenText must both equal " +
                "the segment texts joined by one space. For other decision kinds omit stream_segments. " +
                "Do not invent evidence or include a segment without its supporting claim.";
        AIAgent agent = _agentFactory.Create(functionClient, new ChatClientAgentOptions
        {
            Name = "Ellai",
            UseProvidedChatClientAsIs = true,
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            ChatOptions = new ChatOptions { Instructions = instructions, Tools = tools, MaxOutputTokens = maxOutputTokens }
        });
        var history = messages.Take(messages.Count - 1).ToList();
        var owner = context.Actor?.ActorId;
        var restore = frameworkSessionJson is not null && owner is not null &&
            EllaiFrameworkSessionCodec.IsCurrentScope(frameworkSessionJson, context.SessionId, owner, context.ScopeEpoch);
        var session = restore
            ? await EllaiFrameworkSessionCodec.RestoreAsync(agent, frameworkSessionJson!, context.SessionId,
                owner!, context.ScopeEpoch, history, ct).ConfigureAwait(false)
            : await agent.CreateSessionAsync(cancellationToken: ct).ConfigureAwait(false);
        if (!restore) session.SetInMemoryChatHistory(history);
        async Task SaveSessionAsync()
        {
            if (owner is null) return;
            // Conversation is reauthorized from the journal at the next turn.
            // Do not persist provider tool messages or model-generated history.
            session.SetInMemoryChatHistory([]);
            LastFrameworkSessionJson = await EllaiFrameworkSessionCodec.SerializeAsync(
                agent, session, context.SessionId, owner, context.ScopeEpoch, ct).ConfigureAwait(false);
        }
        var currentMessage = messages[^1];
        var streamedSegments = new List<string>();
        var streamingSuppressed = false;
        try
        {
            if (onTextDelta is null && onVerifiedStreamSegment is null)
            {
                await agent.RunAsync(currentMessage, session, cancellationToken: ct).ConfigureAwait(false);
            }
            else
            {
                // Only complete segments with independently valid claims and
                // current authority may leave the streaming model response.
                var textLength = 0;
                var parser = new EllaiVerifiedSegmentParser();
                var modelCall = 0;
                await foreach (var update in agent.RunStreamingAsync(currentMessage, session, cancellationToken: ct).ConfigureAwait(false))
                {
                    if (update.Text.Length > MaxResponseCharacters - textLength)
                        throw new EllaiFrameworkFailureException("ResponseTooLarge");
                    textLength += update.Text.Length;
                    if (modelCall != guardedClient.CallSequence)
                    {
                        if (streamedSegments.Count != 0) throw new EllaiFrameworkFailureException("InvalidDecision");
                        modelCall = guardedClient.CallSequence;
                        parser = new EllaiVerifiedSegmentParser();
                    }
                    foreach (var parsedSegment in parser.Append(update.Text, evidence))
                    {
                        var segment = parsedSegment.Text;
                        // Private tool evidence may have lost authority since
                        // the tool call. Keep it buffered until the final fence.
                        // Owned-case counts use a server-built decision after a
                        // fresh aggregate read, so model text is not publishable.
                        if (onVerifiedStreamSegment is null || countResult is not null || emptyQueueResult is not null || streamingSuppressed) continue;
                        // A structurally valid sentence without observed evidence can
                        // still contain unsupported or unsafe model output. Wait for
                        // the complete decision and its normal publication checks.
                        if (evidence.Count == 0)
                        {
                            streamingSuppressed = true;
                            continue;
                        }
                        // Evidence-backed prose may be released early only when
                        // it matches a line bound to every cited reference.
                        // Paraphrases still flow through the committed answer.
                        if (evidence.Count != 0 && parsedSegment.Claims.Any(claim =>
                            claim.Evidence.Any(reference => !evidenceSources.TryGetValue(reference, out var sources) ||
                                !sources.Any(source => string.Equals(source, segment, StringComparison.Ordinal)))))
                        {
                            if (streamedSegments.Count != 0) throw new EllaiFrameworkFailureException("InvalidDecision");
                            streamingSuppressed = true;
                            continue;
                        }
                        ct.ThrowIfCancellationRequested();
                        if (guard.FailureCode is not null || !await Current(ct).ConfigureAwait(false))
                            throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.DeniedByPolicy);
                        if (!streamSafeToolObservations && (revalidateStreamObservations is null ||
                            !await revalidateStreamObservations(ct).ConfigureAwait(false)))
                        {
                            throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.DeniedByPolicy);
                        }
                        if (!await Current(ct).ConfigureAwait(false))
                            throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.DeniedByPolicy);
                        if (string.Join(" ", streamedSegments.Append(segment)).Length > EllaiDecisionValidator.MaxDisplayChars)
                            throw new EllaiFrameworkFailureException("ResponseTooLarge");
                        await onVerifiedStreamSegment(segment, ct).ConfigureAwait(false);
                        streamedSegments.Add(segment);
                    }
                }
            }
        }
        catch (EllaiFrameworkFailureException error) when (
            error.Code == EllaiToolReasonCodes.DeniedByPolicy &&
            Volatile.Read(ref invalidDelegation) != 0 && streamedSegments.Count == 0)
        {
            LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
            return null;
        }
        finally { LastModelCallCount = guardedClient.CallSequence + Volatile.Read(ref delegatedModelCalls); }
        if (Volatile.Read(ref invalidDelegation) != 0) { LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy; return null; }
        EllaiDecision? decision = null;
        if (guard.FailureCode is not null) { LastFailureCode = guard.FailureCode; return null; }
        while (countResult is null && spatialResult is null && healthSourceResult is null && emptyQueueResult is null)
        {
            LastModelCallCount = guardedClient.CallSequence + Volatile.Read(ref delegatedModelCalls);
            if (guard.FailureCode is not null) { LastFailureCode = guard.FailureCode; return null; }
            if (!await Current(ct).ConfigureAwait(false)) { LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy; return null; }
            decision = EllaiModelTurnParser.Parse(guardedClient.LastText).Turn?.FinalDecision;
            if (decision is not null && EllaiDecisionValidator.Validate(decision).Count == 0 &&
                !decision.Claims.Any(claim => claim?.Evidence is { } refs && refs.Any(reference => reference is null || !evidence.Contains(reference))) &&
                (decision.PlanProposal is null || !decision.PlanProposal.Actions.Any(action =>
                    !evidence.Contains(action.Obligation) || action.Evidence.Any(reference => !evidence.Contains(reference)))) &&
                !HasKnownMisattribution(decision, evidenceSources))
                break;
            decision = null;
            // A repair may reuse private observations only while their authority
            // remains current. Published segments are checked against the final
            // display and speech text before completion below.
            if (streamedSegments.Count != 0 && !streamSafeToolObservations &&
                (revalidateStreamObservations is null || !await revalidateStreamObservations(ct).ConfigureAwait(false)))
            {
                LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
                return null;
            }
            ct.ThrowIfCancellationRequested();
            if (!await Current(ct).ConfigureAwait(false)) { LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy; return null; }
            // A correction is a model-only framework run in the same session;
            // no tool or new data access is granted during output repair.
            guardedClient.RepairMode = true;
            LastRepairUsed = true;
            try
            {
                var repairInstruction = streamedSegments.Count == 0
                    ? "The previous decision was invalid. Return only a valid final_decision JSON object using the required schema and observed evidence."
                    : "The previous decision was invalid. Return only a valid final_decision JSON object using the required schema and observed evidence. Previously emitted stream segments are immutable: displayText and spokenText must both exactly equal those segment texts joined by one space. Do not add or change any text.";
                await agent.RunAsync(repairInstruction,
                    session, cancellationToken: ct).ConfigureAwait(false);
            }
            finally { LastModelCallCount = guardedClient.CallSequence + Volatile.Read(ref delegatedModelCalls); }
        }
        if (healthSourceResult is not null)
        {
            if (streamedSegments.Count != 0 || toolCalls != 1 || healthSourceRequest is null || healthSourceResult.Truncated ||
                healthSourceResult.HealthSourceReplayScope is not { } originalScope || !originalScope.IsValid() ||
                healthSourceResult.Evidence.Count != 1 || healthSourceResult.Evidence[0] is not
                    { SourceKind: "HealthSourceRegistry", ResourceId: "approved-sources", Version: "metadata/1" } ||
                !await Current(ct).ConfigureAwait(false))
            { LastFailureCode = EllaiToolReasonCodes.Unavailable; return null; }
            async Task<EllaiToolResult> ReadCurrentSources(CancellationToken token) =>
                await (revalidateTool ?? invoke)(context, healthSourceRequest, token).ConfigureAwait(false);
            var fresh = await ReadCurrentSources(ct).ConfigureAwait(false);
            if (!fresh.Allowed || fresh.Truncated || !fresh.Evidence.SequenceEqual(healthSourceResult.Evidence) ||
                EllaiObservedToolResult.ResultFingerprint(fresh) != originalScope.ResultDigest || !await Current(ct).ConfigureAwait(false))
            { LastFailureCode = EllaiToolReasonCodes.Unavailable; return null; }
            decision = new EllaiDecision(EllaiDecision.CurrentSchemaVersion, DecisionKind.Answer,
                fresh.ResultSummary, fresh.ResultSummary,
                [new GroundedClaim("approved-health-sources", fresh.ResultSummary, ClaimKind.Observed, fresh.Evidence)],
                [], [], null, [], null, null);
            if (EllaiDecisionValidator.Validate(decision).Count != 0)
            { LastFailureCode = EllaiToolReasonCodes.Unavailable; return null; }
            async Task<bool> SourcePublicationCurrent(CancellationToken token)
            {
                if (!await Current(token).ConfigureAwait(false)) return false;
                var current = await ReadCurrentSources(token).ConfigureAwait(false);
                return current.Allowed && !current.Truncated && current.Evidence.SequenceEqual(fresh.Evidence) &&
                    EllaiObservedToolResult.ResultFingerprint(current) == originalScope.ResultDigest && await Current(token).ConfigureAwait(false);
            }
            if (onTextDelta is not null && !await PublishValidatedTextAsync(decision.DisplayText, onTextDelta, SourcePublicationCurrent, ct).ConfigureAwait(false))
            { LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy; return null; }
            if (!await SourcePublicationCurrent(ct).ConfigureAwait(false))
            { LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy; return null; }
            await SaveSessionAsync().ConfigureAwait(false);
            return decision;
        }
        if (spatialResult is not null)
        {
            if (streamedSegments.Count != 0 || toolCalls != 1 || spatialRequest is null ||
                spatialResult.Truncated || spatialResult.Evidence.Count != 1 ||
                spatialResult.Evidence[0] is not { SourceKind: "SpatialAggregate", ResourceId: "owned-regions", Version: "region-count/1" } original ||
                !await Current(ct).ConfigureAwait(false))
            { LastFailureCode = EllaiToolReasonCodes.Unavailable; return null; }
            var fresh = await (revalidateTool ?? invoke)(context, spatialRequest, ct).ConfigureAwait(false);
            if (!fresh.Allowed || fresh.Truncated || fresh.Evidence.Count != 1 || fresh.Evidence[0] != original ||
                !await Current(ct).ConfigureAwait(false))
            { LastFailureCode = EllaiToolReasonCodes.Unavailable; return null; }
            decision = new EllaiDecision(EllaiDecision.CurrentSchemaVersion, DecisionKind.Answer,
                fresh.ResultSummary, fresh.ResultSummary,
                [new GroundedClaim("owned-region-counts", fresh.ResultSummary, ClaimKind.Observed, fresh.Evidence)],
                [], [], null, [], null, null);
            if (EllaiDecisionValidator.Validate(decision).Count != 0)
            { LastFailureCode = EllaiToolReasonCodes.Unavailable; return null; }
            async Task<bool> SpatialPublicationCurrent(CancellationToken token)
            {
                if (!await Current(token).ConfigureAwait(false)) return false;
                var current = await (revalidateTool ?? invoke)(context, spatialRequest, token).ConfigureAwait(false);
                return current.Allowed && !current.Truncated && current.Evidence.Count == 1 && current.Evidence[0] == original &&
                    await Current(token).ConfigureAwait(false);
            }
            if (onTextDelta is not null && !await PublishValidatedTextAsync(decision.DisplayText, onTextDelta, SpatialPublicationCurrent, ct).ConfigureAwait(false))
            { LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy; return null; }
            if (!await SpatialPublicationCurrent(ct).ConfigureAwait(false))
            { LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy; return null; }
            await SaveSessionAsync().ConfigureAwait(false);
            return decision;
        }
        if (countResult is not null)
        {
            if (streamedSegments.Count != 0) { LastFailureCode = "InvalidDecision"; return null; }
            if (toolCalls != 1 || !HasCountEvidence(countResult) || countRequest is null ||
                countResult.CountReplayScope is not { } originalScope || !originalScope.IsValid() ||
                !countRequest.Arguments.TryGetValue("window_days", out var requestedWindow) ||
                !int.TryParse(requestedWindow, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var windowDays) || windowDays != originalScope.WindowDays ||
                !await Current(ct).ConfigureAwait(false))
            {
                LastFailureCode = EllaiToolReasonCodes.Unavailable;
                return null;
            }
            // Re-read through the same scoped, authorized broker immediately before
            // publication; the session fence alone does not detect grant changes.
            var fresh = await (revalidateTool ?? invoke)(context, countRequest, ct).ConfigureAwait(false);
            if (!fresh.Allowed || !HasCountEvidence(fresh) ||
                fresh.CountReplayScope != originalScope ||
                !TryCount(countResult.ResultSummary, out var previousCount) ||
                !TryCount(fresh.ResultSummary, out var freshCount) || previousCount != freshCount ||
                !await Current(ct).ConfigureAwait(false))
            {
                LastFailureCode = EllaiToolReasonCodes.Unavailable;
                return null;
            }
            countResult = fresh;
            decision = new EllaiDecision(EllaiDecision.CurrentSchemaVersion, DecisionKind.Answer,
                countResult.ResultSummary, countResult.ResultSummary,
                [new GroundedClaim("owned-case-count", countResult.ResultSummary, ClaimKind.Observed, countResult.Evidence)],
                [], [], null, [], null, null);
            if (EllaiDecisionValidator.Validate(decision).Count != 0)
            {
                LastFailureCode = EllaiToolReasonCodes.Unavailable;
                return null;
            }
            if (!await Current(ct).ConfigureAwait(false))
            {
                LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
                return null;
            }
            var firstCountChunk = true;
            async Task<bool> CountPublicationCurrent(CancellationToken token)
            {
                if (!await Current(token).ConfigureAwait(false)) return false;
                if (firstCountChunk)
                {
                    firstCountChunk = false;
                    return true;
                }
                var currentCount = await (revalidateTool ?? invoke)(context, countRequest, token).ConfigureAwait(false);
                return currentCount.Allowed && HasCountEvidence(currentCount) &&
                    currentCount.CountReplayScope == originalScope &&
                    TryCount(currentCount.ResultSummary, out var value) && value == freshCount &&
                    await Current(token).ConfigureAwait(false);
            }
            if (onTextDelta is not null && !await PublishValidatedTextAsync(decision.DisplayText, onTextDelta, CountPublicationCurrent, ct).ConfigureAwait(false))
            {
                LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
                return null;
            }
            if (!(onTextDelta is null ? await Current(ct).ConfigureAwait(false) : await CountPublicationCurrent(ct).ConfigureAwait(false)))
            {
                LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
                return null;
            }
            await SaveSessionAsync().ConfigureAwait(false);
            return decision;
        }
        if (emptyQueueResult is not null)
        {
            if (streamedSegments.Count != 0 || toolCalls != 1 || emptyQueueRequest is null ||
                !IsEmptyQueueObservation(emptyQueueRequest, emptyQueueResult) || !await Current(ct).ConfigureAwait(false))
            {
                LastFailureCode = EllaiToolReasonCodes.Unavailable;
                return null;
            }
            var fresh = await (revalidateTool ?? invoke)(context, emptyQueueRequest, ct).ConfigureAwait(false);
            if (!IsEmptyQueueObservation(emptyQueueRequest, fresh) || !await Current(ct).ConfigureAwait(false))
            {
                LastFailureCode = EllaiToolReasonCodes.Unavailable;
                return null;
            }
            const string text = "No matching cases were found in your current owned-case queue.";
            decision = new EllaiDecision(EllaiDecision.CurrentSchemaVersion, DecisionKind.Answer, text, text,
                [], [], [], null, [], null, null);
            if (EllaiDecisionValidator.Validate(decision).Count != 0 ||
                onTextDelta is not null && !await PublishValidatedTextAsync(text, onTextDelta, Current, ct).ConfigureAwait(false) ||
                !await Current(ct).ConfigureAwait(false))
            {
                LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
                return null;
            }
            await SaveSessionAsync().ConfigureAwait(false);
            return decision;
        }
        if (decision is null) return null;
        if (!streamSafeToolObservations && (revalidateStreamObservations is null ||
            !await revalidateStreamObservations(ct).ConfigureAwait(false)))
        {
            LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
            return null;
        }
        if (streamedSegments.Count != 0 &&
            (decision.Kind != DecisionKind.Answer ||
             decision.UiIntents.Count != 0 || decision.Questions.Count != 0 || decision.PlanProposal is not null ||
             !string.Equals(string.Join(" ", streamedSegments), decision.DisplayText.Trim(), StringComparison.Ordinal) ||
             !string.Equals(string.Join(" ", streamedSegments), decision.SpokenText?.Trim(), StringComparison.Ordinal)))
        {
            LastFailureCode = "InvalidDecision";
            return null;
        }
        if (!await Current(ct).ConfigureAwait(false))
        {
            LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
            return null;
        }
        async Task<bool> PublicationCurrent(CancellationToken token)
        {
            if (!await Current(token).ConfigureAwait(false)) return false;
            if (!streamSafeToolObservations && (revalidateStreamObservations is null ||
                !await revalidateStreamObservations(token).ConfigureAwait(false))) return false;
            return streamSafeToolObservations || await Current(token).ConfigureAwait(false);
        }
        if (onTextDelta is not null && streamedSegments.Count == 0 && !await PublishValidatedTextAsync(decision.DisplayText, onTextDelta, PublicationCurrent, ct).ConfigureAwait(false))
        {
            LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
            return null;
        }
        if (!await PublicationCurrent(ct).ConfigureAwait(false))
        {
            LastFailureCode = EllaiToolReasonCodes.DeniedByPolicy;
            return null;
        }
        await SaveSessionAsync().ConfigureAwait(false);
        return decision;
    }

    internal static IEnumerable<string> EvidenceLines(string toolName, EllaiToolResult result, EvidenceRef reference)
    {
        var lines = result.ResultSummary.Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0);
        if (toolName == EllaiToolNames.ProductFacts && reference.SourceKind == "ProductFact" &&
            reference.ResourceId == reference.EvidenceId && !string.IsNullOrWhiteSpace(reference.FieldPath))
        {
            var prefix = $"[{reference.ResourceId}; source {reference.FieldPath}; revision {reference.Version}] ";
            foreach (var line in lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)))
            {
                yield return line;
                var fact = line[prefix.Length..].Trim();
                if (fact.Length != 0) yield return fact;
            }
            yield break;
        }
        if (result.Evidence.Count == 1)
        {
            foreach (var line in lines) yield return line;
            yield break;
        }
        if ((toolName is EllaiToolNames.UiCatalogue or EllaiToolNames.UiObserve) &&
            (reference.SourceKind is "UiCatalogue" or "UiObservation"))
        {
            var prefix = $"control {reference.ResourceId} · ";
            foreach (var line in lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)))
                yield return line;
            yield break;
        }
        if (toolName == EllaiToolNames.SurveillanceSignals && reference.SourceKind == "SurveillanceSignal")
        {
            var prefix = $"case {reference.ResourceId} signal {reference.EvidenceId}: ";
            foreach (var line in lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)))
                yield return line;
            yield break;
        }
        if (toolName == EllaiToolNames.ClassificationSnapshot && reference.SourceKind == "CaseClassification")
        {
            var prefix = $"case {reference.ResourceId} classification {reference.EvidenceId}: ";
            foreach (var line in lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)))
                yield return line;
            yield break;
        }
        if (toolName == EllaiToolNames.PublicSessions && reference.SourceKind == "PublicSession")
        {
            var prefix = $"session {reference.ResourceId} · ";
            foreach (var line in lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)))
                yield return line;
            yield break;
        }
        if (toolName == EllaiToolNames.Receipts && reference.SourceKind == "Receipt")
        {
            var prefix = $"receipt {reference.ResourceId} · ";
            foreach (var line in lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)))
                yield return line;
            yield break;
        }
        if (reference.SourceKind == "CaseQueue" &&
            toolName is EllaiToolNames.State or EllaiToolNames.Topic or EllaiToolNames.Evidence &&
            Guid.TryParseExact(reference.ResourceId, "D", out var caseId) &&
            reference.ResourceId == caseId.ToString("D"))
        {
            var prefix = $"case {reference.ResourceId}";
            foreach (var line in lines.Where(line => line.StartsWith(prefix + ";", StringComparison.Ordinal) ||
                line.StartsWith(prefix + " ·", StringComparison.Ordinal)))
                yield return line;
        }
    }

    private static bool HasKnownMisattribution(EllaiDecision decision, IReadOnlyDictionary<EvidenceRef, List<string>> sources)
    {
        bool Misattributed(string value, IReadOnlyList<EvidenceRef> evidence)
        {
            var text = value.Trim();
            bool Matches(EvidenceRef reference, IReadOnlyList<string> lines) =>
                (reference.SourceKind == "ProductFact" || reference.SourceKind == "SurveillanceSignal" &&
                    text.StartsWith($"case {reference.ResourceId} signal {reference.EvidenceId}:", StringComparison.Ordinal) ||
                 (reference.SourceKind is "UiCatalogue" or "UiObservation") &&
                    text.StartsWith($"control {reference.ResourceId} ·", StringComparison.Ordinal) || reference.SourceKind == "CaseClassification" &&
                    text.StartsWith($"case {reference.ResourceId} classification {reference.EvidenceId}:", StringComparison.Ordinal) || reference.SourceKind == "PublicSession" &&
                    text.StartsWith($"session {reference.ResourceId} ·", StringComparison.Ordinal) || reference.SourceKind == "CaseQueue" &&
                    (text.StartsWith($"case {reference.ResourceId};", StringComparison.Ordinal) ||
                     text.StartsWith($"case {reference.ResourceId} ·", StringComparison.Ordinal))) &&
                lines.Contains(text, StringComparer.Ordinal);
            return sources.Any(item => Matches(item.Key, item.Value)) &&
                !evidence.Any(reference => sources.TryGetValue(reference, out var lines) && Matches(reference, lines));
        }
        return decision.Claims.Any(claim => Misattributed(claim.Text, claim.Evidence)) ||
            decision.PlanProposal is { } plan &&
            plan.Actions.Any(action => Misattributed(action.FactualRationale, action.Evidence));
    }

    private static async Task<bool> PublishValidatedTextAsync(string text,
        Func<string, CancellationToken, ValueTask> publish, Func<CancellationToken, Task<bool>> isCurrent,
        CancellationToken ct)
    {
        // The response feed trims each chunk and joins chunks with one space.
        // Split only on a single ordinary space so that joining preserves text.
        var start = 0;
        for (var index = 0; index < text.Length - 2; index++)
        {
            if (text[index] is not ('.' or '?' or '!') || text[index + 1] != ' ' ||
                char.IsWhiteSpace(text[index + 2])) continue;
            ct.ThrowIfCancellationRequested();
            if (!await isCurrent(ct).ConfigureAwait(false)) return false;
            await publish(text[start..(index + 1)], ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            start = index + 2;
        }
        ct.ThrowIfCancellationRequested();
        if (!await isCurrent(ct).ConfigureAwait(false)) return false;
        await publish(text[start..], ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return true;
    }

    private static bool TryCount(string summary, out int count)
    {
        const string marker = " managed case(s) ";
        var end = summary.IndexOf(marker, StringComparison.Ordinal);
        count = 0;
        return end > 0 && int.TryParse(summary.AsSpan(0, end), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out count) && count >= 0;
    }

    private static bool HasCountEvidence(EllaiToolResult result) => result.Allowed && !result.Truncated &&
        result.ToolName == EllaiToolNames.OwnedCaseCount && result.ReasonCode == EllaiToolReasonCodes.Ok &&
        result.Evidence.Count == 1 &&
        result.Evidence[0] is { SourceKind: "DashboardAggregate", ResourceId: "owned-cases" };

    internal static bool IsEmptyQueueObservation(EllaiToolRequest request, EllaiToolResult result) =>
        request.ToolName == EllaiToolNames.State && result.ToolName == request.ToolName && result.Allowed &&
        result.DenialReason is null && result.ReasonCode == EllaiToolReasonCodes.Ok && !result.Truncated &&
        result.ItemCount == 0 && result.Evidence.Count == 0 &&
        EllaiToolContractCatalog.TryValidateArguments(request.ToolName, request.Arguments, out _) &&
        (!request.Arguments.TryGetValue("limit", out var rawLimit) ||
            int.TryParse(rawLimit, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var limit) && limit is >= 1 and <= 20) &&
        EllaiObservedToolResult.From(request, result).CaseListQuery is { } query && query.IsValid() &&
        result.ResultSummary.StartsWith("Owned cases only; review status is not supplied.", StringComparison.Ordinal) &&
        result.ResultSummary.EndsWith("\nThe permitted work queue is empty.", StringComparison.Ordinal);

    private sealed class ModelCallGuard(IChatClient inner, Func<CancellationToken, Task<bool>> isCurrent,
        EllaiFrameworkUsage usage, int maxModelCalls) : DelegatingChatClient(inner)
    {
        public int CallSequence { get; private set; }
        public bool RepairMode { get; set; }
        private readonly System.Text.StringBuilder _lastText = new();
        public string LastText => _lastText.ToString();

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (RepairMode)
            {
                options = options?.Clone() ?? new ChatOptions();
                options.Tools = [];
                options.ToolMode = ChatToolMode.None;
            }
            var boundedMessages = messages.ToArray();
            for (var attempt = 0; ; attempt++)
            {
                var current = await isCurrent(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!current) throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.DeniedByPolicy);
                if (CallSequence >= maxModelCalls)
                    throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.BudgetExceeded);
                using var reservation = usage.TryBeginModelCall(boundedMessages, options);
                if (reservation is null)
                    throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.BudgetExceeded);
                cancellationToken.ThrowIfCancellationRequested();
                CallSequence++;
                ChatResponse? response = null;
                try { response = await EllaiProviderCallIsolation.Instance.ExecuteAsync(
                    token => base.GetResponseAsync(boundedMessages, options, token), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) when (cancellationToken.IsCancellationRequested && error is not OutOfMemoryException)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (System.ClientModel.ClientResultException error) { throw ProviderFailure(error); }
                catch (Exception error) when (error is not OutOfMemoryException) { throw new EllaiFrameworkFailureException("ProviderUnavailable"); }
                finally
                {
                    // Each dispatch, including a truncated one, may be billable.
                    await usage.RecordAsync(response ?? new ChatResponse(), boundedMessages, options, CancellationToken.None).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (IsOutputLimit(response.FinishReason))
                {
                    // The framework has not seen this response, so any incomplete
                    // function calls in it cannot have executed. Retry only once.
                    if (attempt != 0 || options?.MaxOutputTokens is not > 0 or >= 16384)
                        throw new EllaiFrameworkFailureException("ProviderOutputLimit");
                    options = options.Clone();
                    options.MaxOutputTokens = Math.Min(options.MaxOutputTokens!.Value * 2, 16384);
                    continue;
                }
                ValidateToolCalls(response.Messages.SelectMany(message => message.Contents), options);
                var responseText = response.Text;
                if (responseText.Length > MaxResponseCharacters)
                    throw new EllaiFrameworkFailureException("ResponseTooLarge");
                _lastText.Clear().Append(responseText);
                return response;
            }
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var current = await isCurrent(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!current) throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.DeniedByPolicy);
            var boundedMessages = messages.ToArray();
            if (CallSequence >= maxModelCalls)
                throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.BudgetExceeded);
            using var reservation = usage.TryBeginModelCall(boundedMessages, options);
            if (reservation is null)
                throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.BudgetExceeded);
            cancellationToken.ThrowIfCancellationRequested();
            CallSequence++;
            _lastText.Clear();
            UsageDetails? details = null;
            var outputLimitReached = false;
            try
            {
                await foreach (var update in EllaiProviderCallIsolation.Instance.StreamAsync(
                    token => CreateStreamSafe(boundedMessages, options, token), cancellationToken).ConfigureAwait(false))
                {
                    // A provider may return an update after ignoring cancellation.
                    // Never pass that late update to framework tool invocation.
                    cancellationToken.ThrowIfCancellationRequested();
                    outputLimitReached |= IsOutputLimit(update.FinishReason);
                    foreach (var reported in update.Contents.OfType<UsageContent>().Select(content => content.Details))
                    {
                        if (reported is null) continue;
                        details ??= new UsageDetails();
                        details.InputTokenCount = reported.InputTokenCount ?? details.InputTokenCount;
                        details.OutputTokenCount = reported.OutputTokenCount ?? details.OutputTokenCount;
                    }
                    ValidateToolCalls(update.Contents, options);
                    var text = update.Text;
                    if (text.Length > MaxResponseCharacters - _lastText.Length)
                        throw new EllaiFrameworkFailureException("ResponseTooLarge");
                    _lastText.Append(text);
                    yield return update;
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (outputLimitReached)
                    throw new EllaiFrameworkFailureException("ProviderOutputLimit");
            }
            finally
            {
                await usage.RecordAsync(new ChatResponse { Usage = details }, boundedMessages, options, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static void ValidateToolCalls(IEnumerable<AIContent> contents, ChatOptions? options)
        {
            var calls = contents.OfType<FunctionCallContent>().ToArray();
            if (calls.Any(call => call.Exception is not null ||
                call.RawRepresentation is OpenAI.Chat.ChatToolCall raw &&
                EllaiMuseChatClientFactory.HasDuplicateArguments(raw.FunctionArguments.ToMemory())))
                throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.InvalidArguments);
            var allowed = options?.Tools?.OfType<AIFunctionDeclaration>()
                .Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            if (calls.Any(call => string.IsNullOrWhiteSpace(call.Name) || !allowed.Contains(call.Name)))
                throw new EllaiFrameworkFailureException(EllaiToolReasonCodes.UnregisteredTool);
        }

        private IAsyncEnumerator<ChatResponseUpdate> CreateStreamSafe(IReadOnlyList<ChatMessage> messages,
            ChatOptions? options, CancellationToken ct)
        {
            try { return new SafeDisposingEnumerator(base.GetStreamingResponseAsync(messages, options, ct).GetAsyncEnumerator(ct), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (ct.IsCancellationRequested && error is not OutOfMemoryException)
            {
                throw new OperationCanceledException(ct);
            }
            catch (System.ClientModel.ClientResultException error) { throw ProviderFailure(error); }
            catch (Exception error) when (error is not OutOfMemoryException) { throw new EllaiFrameworkFailureException("ProviderUnavailable"); }
        }

        private sealed class SafeDisposingEnumerator(IAsyncEnumerator<ChatResponseUpdate> inner, CancellationToken ct)
            : IAsyncEnumerator<ChatResponseUpdate>
        {
            public ChatResponseUpdate Current => inner.Current;
            public async ValueTask<bool> MoveNextAsync()
            {
                try { return await inner.MoveNextAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) when (ct.IsCancellationRequested && error is not OutOfMemoryException)
                {
                    throw new OperationCanceledException(ct);
                }
                catch (System.ClientModel.ClientResultException error) { throw ProviderFailure(error); }
                catch (Exception error) when (error is not OutOfMemoryException) { throw new EllaiFrameworkFailureException("ProviderUnavailable"); }
            }

            public async ValueTask DisposeAsync()
            {
                try { await inner.DisposeAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) when (ct.IsCancellationRequested && error is not OutOfMemoryException)
                {
                    throw new OperationCanceledException(ct);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    throw new EllaiFrameworkFailureException("ProviderUnavailable");
                }
            }
        }

        private static EllaiFrameworkFailureException ProviderFailure(System.ClientModel.ClientResultException error)
            => new(error.Status switch
            {
                401 or 403 => "ProviderAuthenticationFailed",
                413 => "ProviderRequestTooLarge",
                429 => "ProviderRateLimited",
                _ => "ProviderUnavailable"
            });

        private static bool IsOutputLimit(object? finishReason)
            => string.Equals(finishReason?.ToString(), "Length", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Creates native Agent Framework agents from each run's guarded client and options.</summary>
public interface IEllaiFrameworkAgentFactory
{
    AIAgent Create(IChatClient client, ChatClientAgentOptions options);
}

/// <summary>Default native AIAgent composition used by the DI-owned Ellai factory.</summary>
public sealed class EllaiFrameworkAgentFactory : IEllaiFrameworkAgentFactory
{
    public static EllaiFrameworkAgentFactory Instance { get; } = new();

    public AIAgent Create(IChatClient client, ChatClientAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        return client.AsAIAgent(options);
    }
}

/// <summary>Safe, non-provider diagnostic for an unsuccessful framework call.</summary>
public sealed class EllaiFrameworkFailureException(string code) : InvalidOperationException("Ellai framework execution was stopped.")
{
    public string Code { get; } = code;
}
