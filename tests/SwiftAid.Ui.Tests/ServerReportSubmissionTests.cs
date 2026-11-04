using System.Reflection;
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
        Assert.Contains("could not verify", (string)typeof(Report).GetProperty("_sendError", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("did not identify a field", (string)typeof(Report).GetProperty("_sendError", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!);
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
        typeof(Report).GetProperty("IntakeSubmissionClient", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, client);
        return page;
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
