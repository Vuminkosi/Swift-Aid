using System.Text.Json.Serialization;

namespace SwiftAid.Contracts.Cases;

/// <summary>One eligible permission group, never a union of the recipient's independent grants.</summary>
public sealed record CaseCollaboratorCandidate(string Key, string ActorId, string? DisplayLabel, IReadOnlyList<string> Operations)
{
    [JsonIgnore]
    public string? SafeDisplayLabel => DisplayLabel is { Length: > 0 and <= 120 } label && !label.Any(char.IsControl) ? label : null;
}

public sealed record CaseCollaboratorCandidatePage(IReadOnlyList<CaseCollaboratorCandidate> Items, string? NextCursor);

public interface ICaseCollaboratorCandidatesClient
{
    Task<CaseCollaboratorCandidatePage?> GetAsync(Guid caseId, string? search, string? after, CancellationToken cancellationToken = default, bool followUpAssignees = false);
}
