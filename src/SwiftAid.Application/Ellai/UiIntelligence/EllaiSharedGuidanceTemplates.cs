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
            schema = 1,
            objective = objective.Trim().ToUpperInvariant(),
            audience = authority.Features.Contains(EllaiNavigationManifest.FeatureId) ? "authorized" : "guest",
            features = authority.Features.Order(StringComparer.Ordinal).ToArray(),
            capabilities = authority.Capabilities.Order(StringComparer.Ordinal).ToArray(),
            catalogue,
            controls = observations.Select(o => new { o.ControlId, o.DescriptorVersion, o.State, o.Platform })
                .OrderBy(o => o.ControlId, StringComparer.Ordinal).ToArray(),
            reportAlreadyReceived
        });
        return "ellai:ui-template:v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
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
            var result = await cache.SetAsync(key, bytes, Lifetime, ct).ConfigureAwait(false);
            if (result is { Status: CacheWriteStatus.Failed, FailureCode: "invalid-key-or-ttl" })
                await cache.SetAsync(key, bytes, TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { /* Cache is optional. */ }
    }

    private static EllaiGuidancePlanDraft? Sanitize(EllaiGuidancePlanDraft draft, string objective, UiRegistrySnapshot catalogue)
    {
        if (draft.Status != GuidancePlanStatus.Ready || draft.Mode != GuidanceMode.Navigation ||
            draft.Steps is null || draft.Steps.Count is < 1 or > EllaiGuidancePlanDraft.MaxStepsPerChunk) return null;
        var descriptors = catalogue.Descriptors.ToDictionary(d => d.ControlId, StringComparer.Ordinal);
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
                Explanation = descriptor.Semantics.Purpose, SuggestionIds = []
            });
        }
        return new(EllaiGuidancePlanDraft.CurrentSchemaVersion, GuidancePlanStatus.Ready, objective,
            GuidanceMode.Navigation, steps, [], null);
    }
}
