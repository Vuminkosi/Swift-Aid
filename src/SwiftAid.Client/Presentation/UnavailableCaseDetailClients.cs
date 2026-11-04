namespace SwiftAid.Client.Presentation;

/// <summary>Fail-closed discussion client until an authoritative case discussion endpoint exists.</summary>
public sealed class UnavailableCaseDiscussionClient : ICaseDiscussionClient
{
    private const string Capability = "case-discussion";

    public Task<UiCapabilityResult<CaseDiscussionSnapshot>> GetAsync(
        Guid caseId,
        CaseDiscussionQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (caseId == Guid.Empty || query is null || !query.IsValid)
        {
            return Task.FromResult(Invalid<CaseDiscussionSnapshot>("Check the discussion request."));
        }

        return Task.FromResult(UiCapabilityResults.Unavailable<CaseDiscussionSnapshot>(Capability));
    }

    private static UiCapabilityResult<T> Invalid<T>(string message) => new(
        UiCapabilityResultCode.InvalidRequest,
        default,
        UiCapabilityResults.UnavailableMetadata(Capability, message),
        message);
}

/// <summary>Fail-closed evidence client until an authorized evidence metadata endpoint exists.</summary>
public sealed class UnavailableCaseEvidenceClient : ICaseEvidenceClient
{
    private const string Capability = "case-evidence";

    public Task<UiCapabilityResult<CaseEvidenceSnapshot>> GetAsync(
        Guid caseId,
        CaseEvidenceQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (caseId == Guid.Empty || query is null || !query.IsValid)
        {
            return Task.FromResult(Invalid<CaseEvidenceSnapshot>("Check the evidence request."));
        }

        return Task.FromResult(UiCapabilityResults.Unavailable<CaseEvidenceSnapshot>(Capability));
    }

    public Task<UiCapabilityResult<CaseEvidenceDetail>> GetDetailAsync(
        Guid caseId,
        string evidenceKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (caseId == Guid.Empty || string.IsNullOrWhiteSpace(evidenceKey) || evidenceKey.Length > 160)
        {
            return Task.FromResult(Invalid<CaseEvidenceDetail>("Check the evidence selection."));
        }

        return Task.FromResult(UiCapabilityResults.Unavailable<CaseEvidenceDetail>(Capability));
    }

    public Task<UiCapabilityResult<CaseEvidenceDownload>> DownloadAsync(
        Guid caseId,
        string evidenceKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (caseId == Guid.Empty || string.IsNullOrWhiteSpace(evidenceKey) || evidenceKey.Length > 160)
        {
            return Task.FromResult(Invalid<CaseEvidenceDownload>("Check the evidence selection."));
        }

        return Task.FromResult(UiCapabilityResults.Unavailable<CaseEvidenceDownload>(Capability));
    }

    private static UiCapabilityResult<T> Invalid<T>(string message) => new(
        UiCapabilityResultCode.InvalidRequest,
        default,
        UiCapabilityResults.UnavailableMetadata(Capability, message),
        message);
}

/// <summary>Fail-closed case summary client until an authorized detail projection exists.</summary>
public sealed class UnavailableCaseDiscussionCommandClient : ICaseDiscussionCommandClient
{
    private const string Capability = "case-discussion-commands";

    public Task<UiCapabilityResult<IReadOnlyList<CaseDiscussionMentionTarget>>> GetMentionTargetsAsync(
        Guid caseId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return caseId == Guid.Empty
            ? Task.FromResult(CaseDetailUnavailableResults.Invalid<IReadOnlyList<CaseDiscussionMentionTarget>>(Capability, "Choose a valid case."))
            : Task.FromResult(UiCapabilityResults.Unavailable<IReadOnlyList<CaseDiscussionMentionTarget>>(Capability));
    }

    public Task<UiCapabilityResult<CaseDiscussionPostResult>> AppendAsync(
        CaseDiscussionPostCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return command is null || !command.IsValid
            ? Task.FromResult(CaseDetailUnavailableResults.Invalid<CaseDiscussionPostResult>(Capability, "Check the message and try again."))
            : Task.FromResult(UiCapabilityResults.Unavailable<CaseDiscussionPostResult>(Capability));
    }
}

/// <summary>Fail-closed evidence uploader until an authorized upload and storage path exists.</summary>
public sealed class UnavailableCaseEvidenceUploadClient : ICaseEvidenceUploadClient
{
    private const string Capability = "case-evidence-upload";

    public Task<UiCapabilityResult<CaseEvidenceUploadResult>> UploadAsync(
        CaseEvidenceUploadRequest request,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return request is null || !request.IsValid || content is null || !content.CanRead
            ? Task.FromResult(CaseDetailUnavailableResults.Invalid<CaseEvidenceUploadResult>(Capability, "Check the selected file and try again."))
            : Task.FromResult(UiCapabilityResults.Unavailable<CaseEvidenceUploadResult>(Capability));
    }
}

/// <summary>Fail-closed evidence download until an authorized content path exists.</summary>
public sealed class UnavailableCaseEvidenceContentClient : ICaseEvidenceContentClient
{
    private const string Capability = "case-evidence-content";

    public Task<UiCapabilityResult<CaseEvidenceContentMetadata>> DownloadToAsync(
        Guid caseId,
        string evidenceKey,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return caseId == Guid.Empty || string.IsNullOrWhiteSpace(evidenceKey) || evidenceKey.Length > 160 ||
            destination is null || !destination.CanWrite
            ? Task.FromResult(CaseDetailUnavailableResults.Invalid<CaseEvidenceContentMetadata>(Capability, "Check the evidence selection and destination."))
            : Task.FromResult(UiCapabilityResults.Unavailable<CaseEvidenceContentMetadata>(Capability));
    }
}

/// <summary>Fail-closed observation service until an authorized categorized workflow exists.</summary>
public sealed class UnavailableCaseObservationClient : ICaseObservationClient
{
    private const string Capability = "case-observations";

    public Task<UiCapabilityResult<CaseObservationSnapshot>> GetAsync(
        Guid caseId,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return caseId == Guid.Empty || page is null || !page.IsValid || page.PageNumber > 100_000
            ? Task.FromResult(CaseDetailUnavailableResults.Invalid<CaseObservationSnapshot>(Capability, "Check the observations request."))
            : Task.FromResult(UiCapabilityResults.Unavailable<CaseObservationSnapshot>(Capability));
    }

    public Task<UiCapabilityResult<CaseObservationWriteResult>> RecordAsync(
        CaseObservationCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return command is null || !command.IsValid
            ? Task.FromResult(CaseDetailUnavailableResults.Invalid<CaseObservationWriteResult>(Capability, "Check the observation and try again."))
            : Task.FromResult(UiCapabilityResults.Unavailable<CaseObservationWriteResult>(Capability));
    }
}

internal static class CaseDetailUnavailableResults
{
    public static UiCapabilityResult<T> Invalid<T>(string capability, string message) => new(
        UiCapabilityResultCode.InvalidRequest,
        default,
        UiCapabilityResults.UnavailableMetadata(capability, message),
        message);
}
