namespace SwiftAid.Contracts.Ellai;

/// <summary>Canonical execution order for an accepted, bounded guidance sequence.</summary>
public static class EllaiGuidanceSequence
{
    public static IReadOnlyList<GuidancePlanStep> Order(IReadOnlyList<GuidancePlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count is < 1 or > 40 || steps.Any(step => step is null || string.IsNullOrWhiteSpace(step.StepKey)))
            throw new ArgumentException("Guidance requires a bounded sequence of identified steps.", nameof(steps));
        var byKey = new Dictionary<string, GuidancePlanStep>(StringComparer.Ordinal);
        foreach (var step in steps)
            if (!byKey.TryAdd(step.StepKey, step))
                throw new ArgumentException("Guidance step identities must be unique.", nameof(steps));
        var ordered = new List<GuidancePlanStep>(steps.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? next = steps[0].StepKey;
        while (next is not null)
        {
            if (!byKey.TryGetValue(next, out var step) || !visited.Add(next))
                throw new ArgumentException("Guidance links must resolve without cycles.", nameof(steps));
            ordered.Add(step);
            next = step.NextStepKey;
        }
        if (ordered.Count != steps.Count)
            throw new ArgumentException("Guidance cannot contain unreachable steps.", nameof(steps));
        return ordered.AsReadOnly();
    }
}
