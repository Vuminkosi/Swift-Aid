using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Persistence format for unexecuted proposals. Loading never authorizes an action.</summary>
public static class EllaiUiProposalPayload
{
    public const int MaximumBytes = 16384;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static string? Serialize(IReadOnlyList<UiIntentProposal> proposals)
    {
        if (proposals.Count == 0) return null;
        Validate(proposals);
        var payload = JsonSerializer.Serialize(proposals, Json);
        if (Encoding.UTF8.GetByteCount(payload) > MaximumBytes) throw new ArgumentException("UI proposals exceed the storage limit.");
        return payload;
    }

    public static IReadOnlyList<UiIntentProposal> Deserialize(string payload)
    {
        if (Encoding.UTF8.GetByteCount(payload) > MaximumBytes) throw new ArgumentException("UI proposals exceed the storage limit.");
        var proposals = JsonSerializer.Deserialize<UiIntentProposal[]>(payload, Json)
            ?? throw new ArgumentException("UI proposals must be an array.");
        Validate(proposals);
        return proposals;
    }

    private static void Validate(IReadOnlyList<UiIntentProposal> proposals)
    {
        if (proposals.Count > EllaiDecisionValidator.MaxUiIntents || proposals.Any(proposal =>
            proposal is null || !Enum.IsDefined(proposal.Kind) || string.IsNullOrWhiteSpace(proposal.FeatureId) ||
            proposal.FeatureId.Length > 120 || proposal.ResourceId?.Length > 200 || proposal.ControlId?.Length > 120 ||
            proposal.Arguments is null || proposal.Arguments.Count > 12 || proposal.Arguments.Any(argument =>
                string.IsNullOrWhiteSpace(argument.Key) || argument.Key.Length > 120 || argument.Value is null || argument.Value.Length > 2000)))
            throw new ArgumentException("UI proposal shape is invalid.");
        if (proposals.Count(proposal => proposal.Kind is UiIntentKind.StartGuidance or UiIntentKind.ControlGuidance) > 1 ||
            proposals.Any(proposal => proposal.Kind == UiIntentKind.ControlGuidance && EllaiGuidanceControlProposal.ReadCommand(proposal) is null))
            throw new ArgumentException("Guidance controls require one bounded command and no target or authority arguments.");
    }
}
