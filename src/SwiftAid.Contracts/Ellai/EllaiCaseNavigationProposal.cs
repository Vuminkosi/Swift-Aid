namespace SwiftAid.Contracts.Ellai;

/// <summary>Bounded presentation request. Parsing never authorizes access to a case.</summary>
public static class EllaiCaseNavigationProposal
{
    public static Guid? ReadCaseId(UiIntentProposal? proposal) =>
        proposal is { Kind: UiIntentKind.OpenAuthorizedRecord, FeatureId: "case-detail", ControlId: null } &&
        proposal.Arguments is { Count: 0 } && proposal.ResourceId is { Length: 36 } resource &&
        Guid.TryParseExact(resource, "D", out var caseId) && caseId != Guid.Empty
            ? caseId : null;
}
