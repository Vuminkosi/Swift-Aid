using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Outcome of one background utterance run. <see cref="Started"/>
/// is false only when the run was refused before any model call; the refusals journal a blocked activity event
/// so the composer never goes silently dead.</summary>
public sealed record EllaiUtteranceOutcome(
    bool Started, bool Completed, string? StopReason, string? DisplayText = null, string? FailureCode = null)
{
    /// <summary>The structurally validated reasoning result, retained for authorized downstream
    /// planning. UI proposals are not execution authority and require scope/route validation.</summary>
    public EllaiDecision? Decision { get; init; }

    /// <summary>Internal tool trace; absence does not imply a public-only answer.</summary>
    public IReadOnlyList<EllaiObservedToolResult>? ObservedTools { get; init; }
    public bool HistoryIncluded { get; init; }
    public IReadOnlyList<string>? HistoryTurnIds { get; init; }
    public IReadOnlyList<EllaiObservedToolResult>? InheritedObservations { get; init; }
}

/// <summary>Dependencies for one background run, resolved per scope by the
/// hosting worker. Null activity/journal degrades to local-only progress.</summary>
public sealed record EllaiUtteranceServices(
    IEllaiReasoningExecutor Loop,
    EllaiActivityStatusCoordinator? Activity,
    EllaiSessionEventJournal? Journal,
    EllaiConsentService Consent,
    IEllaiSessionStore? Sessions = null,
    IEllaiConsentStore? ConsentStore = null,
    Func<string, CancellationToken, ValueTask>? OnTextDelta = null,
    IEllaiUtteranceOutboxStore? Outbox = null,
    Action<EllaiActivityEvent>? OnActivity = null,
    Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>>? InvokeToolAsync = null,
    IEllaiLanguageClient? Languages = null,
    SwiftAid.Application.Cases.ICaseQueueQuery? CaseQueue = null,
    EllaiTechnicalDocs? TechnicalDocs = null,
    SwiftAid.Application.Intake.Imports.ICaseImportStore? Imports = null,
    SwiftAid.Application.Intake.Imports.ICaseImportWorkspaceReader? ImportWorkspaces = null,
    SwiftAid.Application.Cases.IDashboardAggregateQuery? Dashboard = null,
    Func<string, CancellationToken, ValueTask>? OnVerifiedStreamSegment = null);

/// <summary>Background execution of one accepted utterance with ledger,
/// activity and journal wiring. Ordinary typed messages need no separate
/// reasoning-consent grant; consent-scoped processing lanes retain their
/// grant check. Pure small talk answers with no activity (RT-53).</summary>
public static class EllaiUtteranceRunner
{
    /// <summary>Processing label recorded when a live consent grant covers
    /// the run: the user's own words with an explicit grant.</summary>
    public const string ConsentedLabel = "consented-nonsensitive";

    public static async Task<EllaiUtteranceOutcome> RunOneAsync(
        EllaiUtteranceWork work,
        EllaiUtteranceServices services,
        int maxTokensPerTurn,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(work.Utterance) || work.Utterance.Length > 8000)
        {
            throw new ArgumentException("Utterance must be 1..8000 characters.", nameof(work));
        }

        if (maxTokensPerTurn is < 1 or > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokensPerTurn));
        }

        if (!EllaiPolicyService.IsContributorEligible(
                work.ProcessingLabel,
                work.DataClassification))
        {
            services.Journal?.Append(work.SessionId, "activity", "blocked",
                "This information cannot be processed by the configured assistant route.", DateTimeOffset.UtcNow);
            return new EllaiUtteranceOutcome(false, false,
                "Utterance classification is unknown or ineligible for the configured route.");
        }

        var now = DateTimeOffset.UtcNow;
        if (!IsOrdinaryTypedMessage(work) && !services.Consent.IsGranted(
                work.SessionId, EllaiConsentDimension.ReasoningText,
                work.ConsentPurpose, work.DataClassification, now))
        {
            services.Journal?.Append(work.SessionId, "activity", "blocked",
                "I can't check that with the current permissions.", now);
            return new EllaiUtteranceOutcome(false, false,
                "Reasoning-text consent is missing; post a consent grant first.");
        }

        var context = new EllaiToolContext(
            work.SessionId, work.ScopeEpoch, work.FeatureId, work.ResourceId,
            EllaiExecutionLimits.MaxToolCalls, work.ContributorEligible,
            work.ConsentVersion, work.DataClassification, work.Actor, work.AllowedTools, work.ConsentPurpose);
        var conversation = services.Outbox is null ? [] : (await services.Outbox.ReadConversationForExecutionAsync(
            work.SessionId, work.Actor.ActorId, work.ClientUtteranceId, ct).ConfigureAwait(false))
            .Where(turn => string.Equals(turn.SessionId, work.SessionId, StringComparison.Ordinal)).ToArray();
        if (services.Outbox is not null && !conversation.Any(turn =>
                string.Equals(turn.ClientUtteranceId, work.ClientUtteranceId, StringComparison.Ordinal)))
            return new(false, false, "The current conversation is unavailable.", FailureCode: "HistoryUnavailable");
        var languages = work.Languages ?? new();
        if (!languages.IsValid()) return new(false, false, "Unsupported language selection.", FailureCode: "InvalidLanguage");
        var reasoningText = work.Utterance;
        if (languages.InputLanguage != "eng")
        {
            if (services.Languages?.IsConfigured != true)
                return new(false, false, "Translation is unavailable.", FailureCode: "TranslationUnavailable");
            using var translationTiming = EllaiPipelineMetrics.Measure("input_translation");
            var translated = await services.Languages.TranslateAsync(new(
                work.CycleId + "-input", work.Utterance, languages.InputLanguage, "eng"), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            reasoningText = translated.Text;
        }
        if (languages.OutputLanguage != "eng" && services.Languages?.IsConfigured != true)
            return new(false, false, "Translation is unavailable.", FailureCode: "TranslationUnavailable");
        string? guidanceState = null;
        if (services.Sessions is not null)
        {
            var session = await services.Sessions.GetAsync(work.SessionId, ct).ConfigureAwait(false);
            if (session is { Status: "Active" } && session.OwnerActorId == work.Actor.ActorId &&
                session.ScopeEpoch == work.ScopeEpoch && session.InteractionEpoch == work.ControllerEpoch)
            {
                var checkpoint = EllaiGuidanceCheckpoint.Deserialize(session.GuidanceJson);
                if (checkpoint?.ScopeEpoch == work.ScopeEpoch && checkpoint.InteractionEpoch <= work.ControllerEpoch)
                    guidanceState = checkpoint.Status;
            }
        }
        var authorizedHistory = services.Loop is IEllaiHistoryAuthorizationRequired
            ? await EllaiAuthorizedConversationContext.BuildAsync(conversation, work.ClientUtteranceId,
                work.Actor, services.CaseQueue, ct, services.TechnicalDocs, services.Imports, services.ImportWorkspaces,
                services.Dashboard, context, services.InvokeToolAsync).ConfigureAwait(false) : null;
        var history = authorizedHistory?.Messages ?? EllaiConversationContext.Build(conversation, work.ClientUtteranceId);
        var request = new EllaiLoopRequest(
            context, reasoningText, ForMode(work.Mode),
            EllaiToolBroker.DescribeCatalogue(work.AllowedTools), maxTokensPerTurn,
            work.CycleId, work.ControllerEpoch,
            history, work.ActivePage, work.InteractionMode, guidanceState);

        async Task<bool> IsExecutionCurrentAsync(CancellationToken fenceCt)
        {
            fenceCt.ThrowIfCancellationRequested();
            // The model thread contains the selected history for the entire run,
            // including later tool and repair calls. Recheck that every included
            // turn is still authorized immediately before those boundaries.
            if (authorizedHistory is { RetainedTurnIds.Count: > 0 })
            {
                var currentHistory = await EllaiAuthorizedConversationContext.BuildAsync(conversation, work.ClientUtteranceId,
                    work.Actor, services.CaseQueue, fenceCt, services.TechnicalDocs, services.Imports, services.ImportWorkspaces,
                    services.Dashboard, context, services.InvokeToolAsync).ConfigureAwait(false);
                fenceCt.ThrowIfCancellationRequested();
                if (!authorizedHistory.IsStillAuthorizedBy(currentHistory))
                    return false;
            }
            if (services.Sessions is null)
            {
                return true;
            }

            if (services.ConsentStore is not null)
            {
                await services.Consent.HydrateAsync(work.SessionId, services.ConsentStore, fenceCt)
                    .ConfigureAwait(false);
                fenceCt.ThrowIfCancellationRequested();
            }

            var current = await services.Sessions.GetAsync(work.SessionId, fenceCt).ConfigureAwait(false);
            fenceCt.ThrowIfCancellationRequested();
            return current is not null
                && string.Equals(current.Status, "Active", StringComparison.Ordinal)
                && current.InteractionEpoch == work.ControllerEpoch
                && string.Equals(current.ScopeEpoch, work.ScopeEpoch, StringComparison.Ordinal)
                && string.Equals(current.OwnerActorId, work.Actor.ActorId, StringComparison.Ordinal)
                && (IsOrdinaryTypedMessage(work) || services.Consent.IsGranted(
                    work.SessionId,
                    EllaiConsentDimension.ReasoningText,
                    work.ConsentPurpose,
                    work.DataClassification,
                    DateTimeOffset.UtcNow));
        }

        if (services.OnVerifiedStreamSegment is not null)
            request = request with { RevalidateStreamObservations = async (observations, segmentCt) =>
            {
                var trace = EllaiToolTrace.SerializeFlattened(observations, false, []);
                if (trace is null) return false;
                var previous = new EllaiConversationTurn(work.SessionId, "stream-evidence", 1,
                    "Evidence", "Evidence", "Completed", 1, DateTimeOffset.UtcNow) { ToolTraceJson = trace };
                var currentTurn = new EllaiConversationTurn(work.SessionId, "stream-current", 2,
                    "Current", null, "Processing", 1, null);
                var checkedEvidence = await EllaiAuthorizedConversationContext.BuildAsync([previous, currentTurn],
                    currentTurn.ClientUtteranceId, work.Actor, services.CaseQueue, segmentCt, services.TechnicalDocs,
                    services.Imports, services.ImportWorkspaces, services.Dashboard, context, services.InvokeToolAsync).ConfigureAwait(false);
                return checkedEvidence.RetainedTurnIds.Contains(previous.ClientUtteranceId, StringComparer.Ordinal);
            }, OnVerifiedStreamSegment = languages.OutputLanguage != "eng" ? null : async (text, segmentCt) =>
            {
                if (await IsExecutionCurrentAsync(segmentCt).ConfigureAwait(false))
                    await services.OnVerifiedStreamSegment(text, segmentCt).ConfigureAwait(false);
            }};

        Task<bool> ReauthorizeAsync(int _, CancellationToken fenceCt) =>
            IsExecutionCurrentAsync(fenceCt);

        var outcome = await services.Loop.RunAsync(
            request,
            ct,
            // The framework executor invokes both callbacks at the same fence.
            // Its current-execution callback already performs the identical
            // session, consent and retained-history checks.
            reauthorizeAsync: services.Loop is IEllaiHistoryAuthorizationRequired || services.Sessions is null && authorizedHistory is null
                ? null : ReauthorizeAsync,
            activity: services.Activity,
            journal: services.Journal,
            isExecutionCurrentAsync: services.Sessions is null && authorizedHistory is null ? null : IsExecutionCurrentAsync,
            onTextDelta: services.OnTextDelta is null || languages.OutputLanguage != "eng"
                ? null
                : async (text, deltaCt) =>
                {
                    var current = await IsExecutionCurrentAsync(deltaCt).ConfigureAwait(false);
                    deltaCt.ThrowIfCancellationRequested();
                    if (current)
                    {
                        await services.OnTextDelta(text, deltaCt).ConfigureAwait(false);
                    }
                },
            onActivity: services.OnActivity,
            invokeToolAsync: services.InvokeToolAsync)
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var answer = outcome.Completed ? outcome.Decision?.DisplayText : null;
        if (!string.IsNullOrWhiteSpace(answer) && languages.OutputLanguage != "eng")
        {
            // Never retry a reasoning/tool loop solely because presentation
            // translation failed after its validated decision. Surface a durable
            // failure, preserving committed tool receipts and idempotency.
            try
            {
                using var translationTiming = EllaiPipelineMetrics.Measure("output_translation");
                var translated = await services.Languages!.TranslateAsync(new(
                    work.CycleId + "-output", answer, "eng", languages.OutputLanguage), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                answer = translated.Text;
            }
            catch (Exception error) when (error is EllaiLanguageProviderException or InvalidDataException or ArgumentException)
            {
                return new(true, false, "The reply could not be translated.", FailureCode: "TranslationUnavailable");
            }
        }
        // In particular, the legacy loop can finish after an in-flight provider
        // request without another iteration gate. Do not persist its answer if
        // the session or retained history lost authority while it was running.
        if (outcome.Completed && !await IsExecutionCurrentAsync(ct).ConfigureAwait(false))
            return new(true, false, "The session or its permissions changed.", FailureCode: EllaiToolReasonCodes.DeniedByPolicy);
        return new EllaiUtteranceOutcome(
            true,
            outcome.Completed,
            outcome.StopReason,
            answer,
            outcome.FailureCode)
        {
            Decision = outcome.Completed ? outcome.Decision : null,
            ObservedTools = outcome.Completed ? outcome.ObservedTools : null,
            HistoryIncluded = history.Count > 0,
            HistoryTurnIds = authorizedHistory?.RetainedTurnIds,
            InheritedObservations = authorizedHistory?.InheritedObservations
        };
    }

    private static EllaiRoleOverlay ForMode(EllaiMode mode) => mode switch
    {
        EllaiMode.ShowMe => new EllaiRoleOverlay("presenter", "Show the permitted view and narrate briefly."),
        EllaiMode.GuideMe => new EllaiRoleOverlay("guide", "Guide the user step by step through the permitted task."),
        EllaiMode.HelpMeWork => new EllaiRoleOverlay("assistant", "Help the user complete the permitted task."),
        EllaiMode.ExploreBranch => new EllaiRoleOverlay("explorer", "Explore the permitted branch and report findings."),
        EllaiMode.ReviewEvidence => new EllaiRoleOverlay("reviewer", "Review the evidence and report what it shows."),
        _ => new EllaiRoleOverlay("ask", "Answer the user's question directly and briefly."),
    };

    private static bool IsOrdinaryTypedMessage(EllaiUtteranceWork work) =>
        string.Equals(work.ConsentPurpose, EllaiConsentService.PublicProductHelpPurpose, StringComparison.Ordinal)
        && string.Equals(work.ProcessingLabel, EllaiProcessingClassification.UserProvidedNonSensitive, StringComparison.Ordinal)
        && string.Equals(work.DataClassification, EllaiProcessingClassification.UserProvidedNonSensitive, StringComparison.Ordinal);
}
