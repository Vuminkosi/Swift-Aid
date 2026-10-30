using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Outcome of one background utterance run. <see cref="Started"/>
/// is false only when the run was refused before any model call (missing
/// reasoning-text consent); the refusals journal a blocked activity event
/// so the composer never goes silently dead.</summary>
public sealed record EllaiUtteranceOutcome(bool Started, bool Completed, string? StopReason);

/// <summary>Dependencies for one background run, resolved per scope by the
/// hosting worker. Null activity/journal degrades to local-only progress.</summary>
public sealed record EllaiUtteranceServices(
    EllaiReasoningLoop Loop,
    EllaiActivityStatusCoordinator? Activity,
    EllaiSessionEventJournal? Journal,
    EllaiConsentService Consent);

/// <summary>Background execution of one accepted utterance: consent-gated
/// reasoning loop with ledger, activity and journal wiring. The utterance
/// reaches the provider only under a live ReasoningText grant for
/// <c>ellai-tool-read</c>; without one the run refuses and projects the
/// blocked template. Pure small talk answers with no activity (RT-53).</summary>
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

        var now = DateTimeOffset.UtcNow;
        if (!services.Consent.IsGranted(
                work.SessionId, EllaiConsentDimension.ReasoningText,
                EllaiToolBroker.ToolReadPurpose, now))
        {
            services.Journal?.Append(work.SessionId, "activity", "blocked",
                "I can't check that with the current permissions.", now);
            return new EllaiUtteranceOutcome(false, false,
                "Reasoning-text consent is missing; post a consent grant first.");
        }

        var context = new EllaiToolContext(
            work.SessionId, work.ScopeEpoch, work.FeatureId, work.ResourceId,
            EllaiCycleRunner.MaxToolCalls, work.ContributorEligible,
            work.ConsentVersion, "general", work.Actor);
        var request = new EllaiLoopRequest(
            context, work.Utterance, ForMode(work.Mode),
            EllaiToolBroker.DescribeCatalogue(), maxTokensPerTurn,
            work.CycleId, work.ControllerEpoch);
        var outcome = await services.Loop.RunAsync(
            request, ct, activity: services.Activity, journal: services.Journal).ConfigureAwait(false);
        return new EllaiUtteranceOutcome(true, outcome.Completed, outcome.StopReason);
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
}
