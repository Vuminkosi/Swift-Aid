using System.Reflection;
using SwiftAid.Client.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Ui.TestHost;

public class UnusedFixtureService : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new InvalidOperationException("Fixture service is unavailable.");
}

public sealed class GuestReportLoadingFixture : IGuestSubmissionStore, IPublicIntakeProcessingPolicyClient
{
    private readonly TaskCompletionSource<UiCapabilityResult<PublicReportProcessingPolicy>> _policy = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<UiCapabilityResult<PublicReportProcessingPolicy>> GetPolicyAsync(CancellationToken cancellationToken = default) => _policy.Task.WaitAsync(cancellationToken);
    public void ReleasePolicy() => _policy.TrySetResult(UiCapabilityResults.Unavailable<PublicReportProcessingPolicy>("fixture"));
    public void ReleaseValidPolicy() => _policy.TrySetResult(new UiCapabilityResult<PublicReportProcessingPolicy>(
        UiCapabilityResultCode.Success, new("/privacy", "1", "Synthetic report-handling terms."),
        new("fixture", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true)));
    public Task<IReadOnlyList<GuestSubmissionSummary>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GuestSubmissionSummary>>([new(Guid.Parse("cccccccc-0000-0000-0000-000000000003"), GuestSubmissionLocalState.Queued, DateTimeOffset.UtcNow, null)]);
    public Task<GuestAudioCleanupRetrySummary> RetryPendingAudioCleanupAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<GuestSubmissionSummary> CreateDraftAsync(IntakeSubmission submission, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<GuestSubmissionSummary> CreateRevisionAsync(Guid previousSubmissionId, IntakeSubmission revisedSubmission, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<GuestAudioRetakeResult> RetakeAudioAsync(Guid submissionId, Guid attachmentId, long expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<GuestSubmissionSummary> QueueAsync(Guid clientSubmissionId, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<GuestSubmissionSummary?> GetAsync(Guid clientSubmissionId, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<GuestDraftDetails?> GetDraftAsync(Guid clientSubmissionId, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
}
