using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai.Guidance;

/// <summary>Reuses the current client's server-authorized case read before generating a fixed local route.</summary>
public static class EllaiAuthorizedCaseNavigation
{
    public static async Task<string?> ResolveAsync(UiIntentProposal proposal, ICaseTimelineClient client, CancellationToken cancellationToken)
    {
        if (EllaiCaseNavigationProposal.ReadCaseId(proposal) is not { } caseId) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var result = await client.GetAsync(caseId, 1, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // A cached model claim, queue row or client-supplied authority flag is never sufficient.
        return result is { Access: CaseTimelineAccess.Found, Timeline: { } timeline } && timeline.Header.CaseId == caseId
            ? $"/case/{caseId:D}" : null;
    }
}
