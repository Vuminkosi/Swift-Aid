using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Versioned, value-only guidance state inside the parent Ellai session.
/// Restoration must revalidate permissions, catalogue and live controls before activation.
/// Evidence/action identifiers reference application proof; they never execute an operation.</summary>
public sealed record EllaiGuidanceCheckpoint(
    string ProposalId,
    string ContextStamp,
    string ScopeEpoch,
    long InteractionEpoch,
    long Version,
    string GenerationId,
    string Status,
    DateTimeOffset LeaseExpiresAtUtc,
    ValidatedGuidancePlan? Plan,
    string? CurrentStepId,
    IReadOnlyList<string> CompletedStepIds,
    IReadOnlyList<string> PendingActionIds,
    string? NavigationId)
{
    public int SchemaVersion { get; init; } = 1;
    public int GenerationAttempt { get; init; } = 1;
    public const int MaximumPayloadBytes = 96 * 1024;
    public const int MaximumStoredPayloadBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static string Serialize(EllaiGuidanceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        checkpoint.Validate();
        var json = JsonSerializer.Serialize(checkpoint, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes)
            throw new ArgumentException("Guidance checkpoint exceeds the storage limit.", nameof(checkpoint));
        return json;
    }

    public static EllaiGuidanceCheckpoint? Deserialize(string? json)
    {
        if (json is null) return null;
        if (Encoding.UTF8.GetByteCount(json) > MaximumStoredPayloadBytes)
            throw new ArgumentException("Guidance checkpoint exceeds the storage limit.", nameof(json));
        var checkpoint = JsonSerializer.Deserialize<EllaiGuidanceCheckpoint>(json, JsonOptions)
            ?? throw new JsonException("Guidance checkpoint cannot be null.");
        checkpoint.Validate();
        return checkpoint;
    }

    private void Validate()
    {
        if (SchemaVersion != 1 || InteractionEpoch < 1 || Version < 1 || GenerationAttempt is < 1 or > 3
            || !Bounded(ProposalId, 512) || !Bounded(ContextStamp, 512) || !Bounded(ScopeEpoch, 512)
            || !Bounded(GenerationId, 120)
            || Status is not ("Generating" or "Ready" or "Paused" or "Completed" or "Cancelled" or "Failed")
            || CompletedStepIds is null || CompletedStepIds.Count > 40
            || PendingActionIds is null || PendingActionIds.Count > 20
            || CompletedStepIds.Any(id => !Bounded(id, 120))
            || PendingActionIds.Any(id => !Bounded(id, 200))
            || CompletedStepIds.Distinct(StringComparer.Ordinal).Count() != CompletedStepIds.Count
            || PendingActionIds.Distinct(StringComparer.Ordinal).Count() != PendingActionIds.Count
            || (NavigationId is not null && !Bounded(NavigationId, 120)))
            throw new ArgumentException("Invalid guidance checkpoint.");

        if (Plan is null)
        {
            if (Status is "Ready" or "Paused" or "Completed" || CurrentStepId is not null || CompletedStepIds.Count != 0)
                throw new ArgumentException("Guidance progress requires an accepted plan.");
            return;
        }

        if (!Bounded(Plan.PlanId, 120) || Plan.Revision < 1 || Plan.Steps is null || Plan.Steps.Count is < 1 or > 40
            || Plan.Steps.Any(step => step is null || !Bounded(step.StepKey, 120)
                || !Bounded(step.ControlId, 120) || !Bounded(step.Explanation, EllaiGuidancePlanDraft.MaxExplanationChars)
                || !Enum.IsDefined(step.Behavior)))
            throw new ArgumentException("Invalid guidance plan checkpoint.");
        var keys = Plan.Steps.Select(step => step.StepKey).ToHashSet(StringComparer.Ordinal);
        if (keys.Count != Plan.Steps.Count || (CurrentStepId is not null && !keys.Contains(CurrentStepId))
            || CompletedStepIds.Any(id => !keys.Contains(id)))
            throw new ArgumentException("Guidance progress must reference accepted plan steps.");
    }

    private static bool Bounded(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
}
