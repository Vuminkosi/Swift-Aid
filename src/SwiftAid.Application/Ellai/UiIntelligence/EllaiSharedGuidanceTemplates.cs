using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwiftAid.Application.Abstractions;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Short-lived, value-free templates. Cached data never grants access or proves screen state.</summary>
internal static class EllaiSharedGuidanceTemplates
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public static string Key(string objective, UiRegistrySnapshot catalogue, EllaiUiEntitlements authority,
        IReadOnlyList<UiControlObservation> observations, bool reportAlreadyReceived)
    {
        var material = JsonSerializer.Serialize(new
        {
            schema = 3,
            objective = objective.Trim(),
            audience = authority.Features.Contains(EllaiNavigationManifest.FeatureId) ? "authorized" : "guest",
            features = authority.Features.Order(StringComparer.Ordinal).ToArray(),
            capabilities = authority.Capabilities.Order(StringComparer.Ordinal).ToArray(),
            catalogue,
            controls = observations.Select(o => new { o.ControlId, o.DescriptorVersion, o.State, o.Platform })
                .OrderBy(o => o.ControlId, StringComparer.Ordinal).ToArray(),
            reportAlreadyReceived
        });
        return "ellai:ui-template:v3:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public static async Task<EllaiGuidancePlanDraft?> ReadAsync(ICacheStore? cache, string key, string objective,
        UiRegistrySnapshot catalogue, CancellationToken ct)
    {
        if (cache is null) return null;
        CacheReadResult read;
        try { read = await cache.GetAsync(key, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
        ct.ThrowIfCancellationRequested();
        if (read.Status != CacheReadStatus.Hit || read.Value is not { Length: > 0 and <= 32768 } bytes) return null;
        try
        {
            var draft = JsonSerializer.Deserialize<EllaiGuidancePlanDraft>(bytes);
            return draft is null ? null : Sanitize(draft, objective, catalogue);
        }
        catch (JsonException) { return null; }
        catch (ArgumentException) { return null; }
    }

    public static async Task WriteAsync(ICacheStore? cache, string key, EllaiGuidancePlanDraft draft,
        UiRegistrySnapshot catalogue, CancellationToken ct)
    {
        if (cache is null) return;
        var safe = Sanitize(draft, "shared-template", catalogue);
        if (safe is null) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(safe);
        if (bytes.Length > 32768) return;
        try
        {
            var seconds = (int)Lifetime.TotalSeconds;
            while (true)
            {
                var result = await cache.SetAsync(key, bytes, TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
                if (result is not { Status: CacheWriteStatus.Failed, FailureCode: "invalid-key-or-ttl" } || seconds == 1)
                    break;
                seconds = Math.Max(1, seconds / 2);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { /* Cache is optional. */ }
    }

    private static EllaiGuidancePlanDraft? Sanitize(EllaiGuidancePlanDraft draft, string objective, UiRegistrySnapshot catalogue)
    {
        if (draft.Status != GuidancePlanStatus.Ready || draft.Mode != GuidanceMode.Navigation ||
            draft.Steps is null || draft.Steps.Count is < 1 or > EllaiGuidancePlanDraft.MaxStepsPerChunk) return null;
        var descriptors = catalogue.Descriptors.ToDictionary(d => d.ControlId, StringComparer.Ordinal);
        var suggestionIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var suggestions = new Dictionary<string, GuidanceStepSuggestion>(StringComparer.Ordinal);
        foreach (var pair in (draft.Suggestions ?? new Dictionary<string, GuidanceStepSuggestion>()).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (suggestions.Count == 12) break;
            if (pair.Value is not { } suggestion || !descriptors.TryGetValue(suggestion.ControlId, out var target) ||
                target.RiskClass == UiRiskClass.R5 || target.ObjectiveId != suggestion.ObjectiveId ||
                string.IsNullOrWhiteSpace(target.Semantics?.Purpose)) continue;
            var label = target.Semantics.Purpose.Trim();
            if (label.Length > 80) label = label[..80].TrimEnd();
            var id = $"suggestion-{suggestions.Count + 1}";
            suggestionIds[pair.Key] = id;
            suggestions[id] = new GuidanceStepSuggestion(label, target.ControlId, target.ObjectiveId!);
        }
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < draft.Steps.Count; i++)
            if (draft.Steps[i] is not { } step || string.IsNullOrWhiteSpace(step.StepKey) ||
                !ids.TryAdd(step.StepKey, $"step-{i + 1}")) return null;
        var steps = new List<GuidancePlanStep>();
        foreach (var step in draft.Steps)
        {
            if (!descriptors.TryGetValue(step.ControlId, out var descriptor) ||
                string.IsNullOrWhiteSpace(descriptor.Semantics?.Purpose) || step.ResourceBindingKey is not null ||
                step.UiIntentBindingId is not null && !descriptor.AllowedIntentBindings.Contains(step.UiIntentBindingId) ||
                step.NextStepKey is not null && !ids.ContainsKey(step.NextStepKey)) return null;
            steps.Add(step with
            {
                StepKey = ids[step.StepKey], NextStepKey = step.NextStepKey is null ? null : ids[step.NextStepKey],
                Explanation = descriptor.Semantics.Purpose,
                SuggestionIds = (step.SuggestionIds ?? []).Where(suggestionIds.ContainsKey)
                    .Select(id => suggestionIds[id]).Distinct(StringComparer.Ordinal).Take(3).ToArray()
            });
        }
        return new EllaiGuidancePlanDraft(EllaiGuidancePlanDraft.CurrentSchemaVersion, GuidancePlanStatus.Ready, objective,
            GuidanceMode.Navigation, steps, [], null) { Suggestions = suggestions };
    }
}
