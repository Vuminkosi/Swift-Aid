using System.Reflection;
using SwiftAid.Client.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Intake;
using SwiftAid.Gateway.Components.Pages;
using SwiftAid.UI.Components.Public;

namespace SwiftAid.Ui.Tests;

public sealed class ServerReportSubmissionTests
{
    [Fact]
    public async Task RetriesUseOneNonemptyIdentityAndOnlyMatchingReceiptIsAccepted()
    {
        var client = new FakeClient();
        var page = CreatePage(client);
        var draft = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human, Narrative = "Synthetic concern" };
        var submission = draft.ToSubmission(null, null, null, [], false);
        Assert.Equal(Guid.Empty, submission.ClientSubmissionId);

        client.ReturnForeignReceipt = true;
        await Send(page, submission);
        Assert.Null(Field<IntakeReceipt?>(page, "_receipt"));
        Assert.Contains("has not confirmed receiving", (string)typeof(Report).GetProperty("_sendError", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!, StringComparison.OrdinalIgnoreCase);
        client.ReturnForeignReceipt = false;
        await Send(page, submission);

        Assert.Equal(2, client.Submissions.Count);
        Assert.NotEqual(Guid.Empty, client.Submissions[0].ClientSubmissionId);
        Assert.Equal(client.Submissions[0].ClientSubmissionId, client.Submissions[1].ClientSubmissionId);
        Assert.Equal(client.Submissions[1].ClientSubmissionId, Field<IntakeReceipt>(page, "_receipt").ClientSubmissionId);

        var nextPage = CreatePage(client);
        await Send(nextPage, submission);
        Assert.NotEqual(client.Submissions[0].ClientSubmissionId, client.Submissions[2].ClientSubmissionId);
    }

    [Fact]
    public async Task CancelledSubmissionDoesNotClaimItWasNotReceived()
    {
        var client = new FakeClient { Cancelled = true };
        var page = CreatePage(client);
        var draft = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human, Narrative = "Synthetic concern" };
        await Send(page, draft.ToSubmission(null, null, null, [], false));
        var error = (string)typeof(Report).GetProperty("_sendError", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        Assert.Contains("could not confirm", error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Field<IntakeReceipt?>(page, "_receipt"));
    }

    [Fact]
    public async Task LateServerRejectionDoesNotApplyToEditedDraft()
    {
        var client = new FakeClient { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var page = CreatePage(client);
        var draft = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human, Narrative = "Synthetic concern" };
        var pending = Send(page, draft.ToSubmission(null, null, null, [], false));
        Field<PublicReportDraft>(page, "_mobileDraft").Narrative = "Newer synthetic edit";
        client.Pending.SetResult(new UiCapabilityResult<IntakeSubmissionOutcome>(UiCapabilityResultCode.InvalidRequest, null,
            UiCapabilityResults.UnavailableMetadata("fixture", "Invalid")));
        await pending;
        Assert.Equal(0, Field<long>(page, "_activeServerValidationRevision"));
        Assert.Contains("did not identify a specific field", (string)typeof(Report).GetProperty("_sendError", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!);
    }

    [Fact]
    public async Task ServerRejectionCreatesANewGuidanceRevisionOnEachAttempt()
    {
        var client = new FakeClient { Invalid = true };
        var page = CreatePage(client);
        var draft = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human, Narrative = "Synthetic concern" };
        var submission = draft.ToSubmission(null, null, null, [], false);
        await Send(page, submission);
        Assert.Equal(1, Field<long>(page, "_activeServerValidationRevision"));
        await Send(page, submission);
        Assert.Equal(2, Field<long>(page, "_activeServerValidationRevision"));
    }

    private static Report CreatePage(FakeClient client)
    {
        var page = new Report();
        var store = DispatchProxy.Create<IGuestSubmissionStore, SubmissionStoreProxy>();
        var proxy = (SubmissionStoreProxy)(object)store;
        typeof(Report).GetProperty("SubmissionStore", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, store);
        typeof(Report).GetProperty("SubmissionCoordinator", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(page, new Coordinator(proxy, client));
        foreach (var name in new[] { "_storageAvailable", "_countryCatalogueIsCurrent", "_metadataAvailable", "_processingPolicyIsLive" })
            typeof(Report).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, true);
        foreach (var name in new[] { "_privacyNoticeReference", "_privacyNoticeVersion", "_processingPolicyReference", "_processingPolicyVersion" })
            typeof(Report).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, "synthetic-v1");
        return page;
    }

    public class SubmissionStoreProxy : DispatchProxy
    {
        public IntakeSubmission? Submission;
        public GuestSubmissionSummary? Summary;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "CreateDraftAsync":
                case "CreateRevisionAsync":
                    Submission = (IntakeSubmission)args![method.Name == "CreateDraftAsync" ? 0 : 1]!;
                    Summary = new(Submission.ClientSubmissionId, GuestSubmissionLocalState.Draft, DateTimeOffset.UtcNow, null);
                    return Task.FromResult(Summary);
                case "QueueAsync":
                    Summary = Summary! with { State = GuestSubmissionLocalState.Queued };
                    return Task.FromResult(Summary);
                case "GetAsync": return Task.FromResult(Summary);
                case "GetEditableDraftAsync": return Task.FromResult<GuestEditableDraftDetails?>(null);
                case "GetAllAsync": return Task.FromResult<IReadOnlyList<GuestSubmissionSummary>>(Summary is null ? [] : [Summary]);
                default: throw new NotSupportedException(method.Name);
            }
        }
    }

    private sealed class Coordinator(SubmissionStoreProxy store, FakeClient client) : IGuestSubmissionCoordinator
    {
        public async Task<GuestSyncSummary> SubmitPendingAsync(CancellationToken cancellationToken = default)
        {
            var result = await client.SubmitAsync(store.Submission!, cancellationToken);
            if (result.Code == UiCapabilityResultCode.Cancelled) throw new OperationCanceledException();
            store.Summary = store.Summary! with
            {
                State = result.Code == UiCapabilityResultCode.InvalidRequest ? GuestSubmissionLocalState.NeedsAttention : GuestSubmissionLocalState.Received,
                Receipt = result.Value?.Receipt
            };
            return new(1, 0, 0, 0);
        }
        public Task<GuestReceiptRefreshSummary> RefreshReceiptsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static Task Send(Report page, IntakeSubmission submission) =>
        (Task)typeof(Report).GetMethod("SendReportAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [submission])!;

    private static T Field<T>(Report page, string name) =>
        (T)typeof(Report).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;

    private sealed class FakeClient : IPublicIntakeSubmissionClient
    {
        public bool ReturnForeignReceipt { get; set; }
        public bool Cancelled { get; set; }
        public bool Invalid { get; set; }
        public TaskCompletionSource<UiCapabilityResult<IntakeSubmissionOutcome>>? Pending { get; set; }
        public List<IntakeSubmission> Submissions { get; } = [];

        public Task<UiCapabilityResult<IntakeSubmissionOutcome>> SubmitAsync(IntakeSubmission submission, CancellationToken cancellationToken = default)
        {
            Submissions.Add(submission);
            if (Pending is not null) return Pending.Task;
            if (Cancelled)
                return Task.FromResult(new UiCapabilityResult<IntakeSubmissionOutcome>(UiCapabilityResultCode.Cancelled, null,
                    UiCapabilityResults.UnavailableMetadata("fixture", "Interrupted")));
            if (Invalid)
                return Task.FromResult(new UiCapabilityResult<IntakeSubmissionOutcome>(UiCapabilityResultCode.InvalidRequest, null,
                    UiCapabilityResults.UnavailableMetadata("fixture", "Invalid")));
            var receipt = new IntakeReceipt(Guid.NewGuid(), ReturnForeignReceipt ? Guid.NewGuid() : submission.ClientSubmissionId,
                IntakeStatus.ReceivedUnverified, IntakeRoutingStatus.NeedsRouting, DateTimeOffset.UtcNow);
            return Task.FromResult(new UiCapabilityResult<IntakeSubmissionOutcome>(UiCapabilityResultCode.Success,
                new(IntakeSubmissionOutcomeKind.Received, submission.ClientSubmissionId, receipt, "Received"),
                UiCapabilityResults.SyntheticMetadata("fixture", "Synthetic")));
        }
    }
}
