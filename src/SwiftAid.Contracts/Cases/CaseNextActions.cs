namespace SwiftAid.Contracts.Cases;

/// <summary>
/// Derives outstanding staff actions from a loaded timeline payload. Pure and
/// synchronous: the hosting page loads the timeline once and shares it.
/// </summary>
public static class CaseNextActions
{
    public static IReadOnlyList<string> Derive(CaseTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var actions = new List<string>();

        if (timeline.Entries.Count == 0)
        {
            actions.Add("No recorded history — resolution state unknown.");
            return actions;
        }

        // Unresolved status: only authoritative resolution events move case state.
        // A case with no authoritative resolution, or one reopened after its last
        // resolution, still has an outstanding resolution decision.
        var resolved = false;
        var sawResolutionEvent = false;
        foreach (var entry in timeline.Entries)
        {
            if (entry.Kind != CaseTimelineEntryKind.Authoritative)
            {
                continue;
            }

            if (string.Equals(entry.EventType, "CaseResolved", StringComparison.Ordinal))
            {
                sawResolutionEvent = true;
                resolved = true;
            }
            else if (string.Equals(entry.EventType, "CaseReopened", StringComparison.Ordinal))
            {
                sawResolutionEvent = true;
                resolved = false;
            }
        }

        if (!sawResolutionEvent || !resolved)
        {
            actions.Add("Case is unresolved — a resolution decision is still outstanding.");
        }

        // Pending reviews: a requested protected action with no later review event
        // may still need review. Matching is positional (a later review closes any
        // earlier request), so multiple interleaved requests degrade to a cautious
        // "check the full history" note rather than a precise per-request verdict.
        var reviewedAfter = new bool[timeline.Entries.Count];
        var sawReview = false;
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            reviewedAfter[i] = sawReview;
            if (timeline.Entries[i].Kind == CaseTimelineEntryKind.Authoritative &&
                string.Equals(timeline.Entries[i].EventType, "CaseActionRequestReviewed", StringComparison.Ordinal))
            {
                sawReview = true;
            }
        }

        const int maximumReviewItems = 5;
        var pendingRequests = 0;
        for (var i = 0; i < timeline.Entries.Count && pendingRequests < maximumReviewItems; i++)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != CaseTimelineEntryKind.Authoritative ||
                !string.Equals(entry.EventType, "CaseActionRequested", StringComparison.Ordinal) ||
                reviewedAfter[i])
            {
                continue;
            }

            pendingRequests++;
            actions.Add($"Action requested ({entry.Label}) — check the full history for its review.");
        }

        // Outstanding follow-ups: a scheduled follow-up with no later outcome event
        // may still need doing. Matching is positional (a later outcome closes any
        // earlier schedule), mirroring the pending-review style. Outcome wording
        // never implies care was completed: missed, unreachable, and rescheduled
        // outcomes only record what was observed about the attempt.
        var outcomeAfter = new bool[timeline.Entries.Count];
        var sawOutcome = false;
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            outcomeAfter[i] = sawOutcome;
            if (timeline.Entries[i].Kind == CaseTimelineEntryKind.Authoritative &&
                string.Equals(timeline.Entries[i].EventType, "CaseFollowUpOutcomeRecorded", StringComparison.Ordinal))
            {
                sawOutcome = true;
            }
        }

        const int maximumFollowUpItems = 5;
        var pendingFollowUps = 0;
        for (var i = 0; i < timeline.Entries.Count && pendingFollowUps < maximumFollowUpItems; i++)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != CaseTimelineEntryKind.Authoritative ||
                !string.Equals(entry.EventType, "CaseFollowUpScheduled", StringComparison.Ordinal) ||
                outcomeAfter[i])
            {
                continue;
            }

            pendingFollowUps++;
            actions.Add($"Follow-up scheduled ({entry.Label}) — outcome not yet recorded; check the full history for its outcome.");
        }

        // Outstanding escalations: a raised escalation with no later acknowledgement
        // may still need a response. Matching is positional (a later acknowledgement
        // closes any earlier raise), mirroring the pending-review style.
        var acknowledgedAfter = new bool[timeline.Entries.Count];
        var sawAcknowledgement = false;
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            acknowledgedAfter[i] = sawAcknowledgement;
            if (timeline.Entries[i].Kind == CaseTimelineEntryKind.Authoritative &&
                string.Equals(timeline.Entries[i].EventType, "CaseEscalationAcknowledged", StringComparison.Ordinal))
            {
                sawAcknowledgement = true;
            }
        }

        const int maximumEscalationItems = 5;
        var pendingEscalations = 0;
        for (var i = 0; i < timeline.Entries.Count && pendingEscalations < maximumEscalationItems; i++)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != CaseTimelineEntryKind.Authoritative ||
                !string.Equals(entry.EventType, "CaseEscalationRaised", StringComparison.Ordinal) ||
                acknowledgedAfter[i])
            {
                continue;
            }

            pendingEscalations++;
            actions.Add($"Escalation raised ({entry.Label}) — acknowledgement not yet recorded; check the full history for its acknowledgement.");
        }

        // Latest-observation presence: note when the recorded history includes at
        // least one observation, without restating sealed content.
        CaseTimelineEntry? latestObservation = null;
        foreach (var entry in timeline.Entries)
        {
            if (entry.Kind == CaseTimelineEntryKind.Authoritative &&
                string.Equals(entry.EventType, "CaseObservationRecorded", StringComparison.Ordinal))
            {
                latestObservation = entry;
            }
        }

        if (latestObservation is not null)
        {
            actions.Add($"Latest observation recorded ({latestObservation.Label}).");
        }

        return actions;
    }
}
