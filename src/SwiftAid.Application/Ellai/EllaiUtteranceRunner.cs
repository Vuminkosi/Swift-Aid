using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Outcome of one background utterance run. <see cref="Started"/>
/// is false only when the run was refused before any model call; the refusals journal a blocked activity event
/// so the composer never goes silently dead.</summary>
public sealed record EllaiUtteranceOutcome(
    bool Started, bool Completed, string? StopReason, string? DisplayText = null, string? FailureCode = null);

/// <summary>Dependencies for one background run, resolved per scope by the
/// hosting worker. Null activity/journal degrades to local-only progress.</summary>
public sealed record EllaiUtteranceServices(
    EllaiReasoningLoop Loop,
    EllaiActivityStatusCoordinator? Activity,
    EllaiSessionEventJournal? Journal,
    EllaiConsentService Consent,
    IEllaiSessionStore? Sessions = null,
    IEllaiConsentStore? ConsentStore = null,
    Func<string, CancellationToken, ValueTask>? OnTextDelta = null,
    IEllaiUtteranceOutboxStore? Outbox = null);

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
            EllaiCycleRunner.MaxToolCalls, work.ContributorEligible,
            work.ConsentVersion, work.DataClassification, work.Actor, work.AllowedTools, work.ConsentPurpose);
        var conversation = services.Outbox is null ? [] : await services.Outbox.ReadConversationAsync(
            work.SessionId, work.Actor.ActorId, ct).ConfigureAwait(false);
        var request = new EllaiLoopRequest(
            context, work.Utterance, ForMode(work.Mode),
            EllaiToolBroker.DescribeCatalogue(work.AllowedTools), maxTokensPerTurn,
            work.CycleId, work.ControllerEpoch,
            EllaiConversationContext.Build(conversation, work.ClientUtteranceId));

        async Task<bool> IsExecutionCurrentAsync(CancellationToken fenceCt)
        {
            if (services.Sessions is null)
            {
                return true;
            }

            if (services.ConsentStore is not null)
            {
                await services.Consent.HydrateAsync(work.SessionId, services.ConsentStore, fenceCt)
                    .ConfigureAwait(false);
            }

            var current = await services.Sessions.GetAsync(work.SessionId, fenceCt).ConfigureAwait(false);
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

        Task<bool> ReauthorizeAsync(int _, CancellationToken fenceCt) =>
            IsExecutionCurrentAsync(fenceCt);

        var outcome = await services.Loop.RunAsync(
            request,
            ct,
            reauthorizeAsync: services.Sessions is null ? null : ReauthorizeAsync,
            activity: services.Activity,
            journal: services.Journal,
            isExecutionCurrentAsync: services.Sessions is null ? null : IsExecutionCurrentAsync,
            onTextDelta: services.OnTextDelta is null
                ? null
                : async (text, deltaCt) =>
                {
                    if (await IsExecutionCurrentAsync(deltaCt).ConfigureAwait(false))
                    {
                        await services.OnTextDelta(text, deltaCt).ConfigureAwait(false);
                    }
                })
            .ConfigureAwait(false);
        return new EllaiUtteranceOutcome(
            true,
            outcome.Completed,
            outcome.StopReason,
            outcome.Completed ? outcome.Decision?.DisplayText : null,
            outcome.FailureCode);
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
