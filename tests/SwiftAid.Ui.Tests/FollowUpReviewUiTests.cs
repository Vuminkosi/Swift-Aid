using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Contracts.Cases;
using SwiftAid.UI.Components.Cases;

namespace SwiftAid.Ui.Tests;

public sealed class FollowUpReviewUiTests
{
    [Fact]
    public async Task UnknownOutcomeRetryReusesCommandAndReportsRecordedReview()
    {
        var client = new FakeReviewClient();
        using var services = new ServiceCollection().AddSingleton<IFollowUpRecommendationReviewClient>(client).BuildServiceProvider();
        var caseId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var component = new CaseTimelineView();
        typeof(CaseTimelineView).GetProperty("Services", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(component, services);
        typeof(CaseTimelineView).GetProperty(nameof(CaseTimelineView.CaseId))!.SetValue(component, caseId);
        typeof(CaseTimelineView).GetProperty(nameof(CaseTimelineView.Client))!.SetValue(component, new TimelineClient(caseId));
        var method = typeof(CaseTimelineView).GetMethod("RecordReviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(component, [activityId])!;
        Assert.Contains("couldn't confirm", (string)typeof(CaseTimelineView).GetField("_reviewMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!, StringComparison.Ordinal);
        await (Task)method.Invoke(component, [activityId])!;
        Assert.Equal(2, client.CommandIds.Count);
        Assert.Equal(client.CommandIds[0], client.CommandIds[1]);
        Assert.Contains("Review recorded. No follow-up action was submitted", (string)typeof(CaseTimelineView).GetField("_reviewMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!, StringComparison.Ordinal);
    }

    private sealed class FakeReviewClient : IFollowUpRecommendationReviewClient
    {
        public List<Guid> CommandIds { get; } = [];
        public Task<FollowUpReviewResult> ReviewAsync(Guid caseId, Guid recommendationId, Guid commandId, CancellationToken cancellationToken = default)
        {
            CommandIds.Add(commandId);
            return Task.FromResult(new FollowUpReviewResult(CommandIds.Count == 1 ? FollowUpReviewStatus.Unavailable : FollowUpReviewStatus.Replayed));
        }
    }

    private sealed class TimelineClient(Guid caseId) : ICaseTimelineClient
    {
        public Task<CaseTimelineResult> GetAsync(Guid requestedCaseId, int? limit, CancellationToken cancellationToken = default, long? beforeAggregateVersion = null) =>
            Task.FromResult(new CaseTimelineResult(CaseTimelineAccess.Found,
                new CaseTimeline(new CaseTimelineHeader(caseId, "org", "workspace", "SZ", "Active", "actor", 2, 1, DateTimeOffset.UtcNow), [])));
    }
}
