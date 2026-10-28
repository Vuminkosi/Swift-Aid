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

        return actions;
    }
}
