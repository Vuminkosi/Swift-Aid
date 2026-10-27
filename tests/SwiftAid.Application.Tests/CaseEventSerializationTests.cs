using System.Text;
using System.Text.Json;
using SwiftAid.Application.Serialization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Tests;

public sealed class CaseEventSerializationTests
{
    private static readonly CaseEventSerializer Serializer = new();
    private static readonly DateTimeOffset OccurredAtUtc = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RecordedAtUtc = new(2026, 10, 26, 8, 1, 0, TimeSpan.Zero);
    private static readonly Guid CaseId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid InitialCommandId = Guid.Parse("20000000-0000-0000-0000-000000000001");

    public static IEnumerable<object[]> GoldenEvents()
    {
        var report = new ReportFacts(
            CaseKind.Human,
            Sector.HumanHealth,
            "reported details",
            [new ReportedObservation("reported observation")]);
        var initialReport = new CaseReported(
            CaseId, 1, 1, InitialCommandId, "officer-1", OccurredAtUtc, RecordedAtUtc, report, "workspace-1");
        var requestId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var requestCommandId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var request = new CaseActionRequested(
            CaseId, 3, 1, requestCommandId, "worker-1", null, RecordedAtUtc, requestId,
            CaseAction.Reassign, "request transfer", ["evidence-1"], "worker-2");
        var approvedCommandId = Guid.Parse("20000000-0000-0000-0000-000000000003");
        var acceptedAssignment = new CaseAssigned(
            CaseId, 4, 1, approvedCommandId, "officer-1", null, RecordedAtUtc, "worker-2");

        yield return [initialReport, "case-reported.v1.json"];
        yield return [acceptedAssignment, "case-assigned.v1.json"];
        yield return [new CaseReportCorrected(
            CaseId, 3, 2, Guid.Parse("20000000-0000-0000-0000-000000000004"), "officer-1", null,
            RecordedAtUtc, report with { Narrative = "corrected details" }, "reporter clarification", 1),
            "case-report-corrected.v1.json"];
        yield return [request, "case-action-requested.v1.json"];
        yield return [new CaseActionRequestReviewed(
            CaseId, 5, 1, approvedCommandId, "officer-1", null, RecordedAtUtc, requestId,
            CaseActionReviewOutcome.Approved, acceptedAssignment.EventId, "authorized transfer"),
            "case-action-review-approved.v1.json"];
        yield return [new CaseActionRequestReviewed(
            CaseId, 4, 1, Guid.Parse("20000000-0000-0000-0000-000000000005"), "officer-1", null,
            RecordedAtUtc, requestId, CaseActionReviewOutcome.Denied, null, "request declined"),
            "case-action-review-denied.v1.json"];
        yield return [new CaseResolved(
            CaseId, 3, 1, Guid.Parse("20000000-0000-0000-0000-000000000006"), "officer-1", OccurredAtUtc,
            RecordedAtUtc, "administrative work complete"),
            "case-resolved.v1.json"];
        yield return [new CaseReopened(
            CaseId, 4, 1, Guid.Parse("20000000-0000-0000-0000-000000000007"), "officer-1", null,
            RecordedAtUtc, "new support work", "worker-1"),
            "case-reopened.v1.json"];
    }

    [Theory]
    [MemberData(nameof(GoldenEvents))]
    public void TypedSerializer_MatchesCommittedGoldenFixture(CaseEvent @event, string fixtureName)
    {
        var actual = Serializer.SerializeToUtf8Bytes(@event);
        var fixturePath = Path.Combine(FixtureDirectory, fixtureName);
        var expected = File.ReadAllText(fixturePath).Replace("\r\n", "\n", StringComparison.Ordinal);
        var actualText = Encoding.UTF8.GetString(actual);

        Assert.DoesNotContain('\r', actualText);
        Assert.EndsWith("\n", actualText);
        Assert.Equal(expected, actualText);
    }

    [Theory]
    [InlineData(CaseActionReviewOutcome.Pending)]
    [InlineData(CaseActionReviewOutcome.Denied)]
    public void NonApprovedReview_SerializesWithNullLinkAllowedBySchema(CaseActionReviewOutcome outcome)
    {
        var review = new CaseActionRequestReviewed(
            CaseId, 4, 1, Guid.Parse("20000000-0000-0000-0000-000000000008"), "officer-1", null,
            RecordedAtUtc, Guid.Parse("30000000-0000-0000-0000-000000000002"), outcome, null, "review not approved");

        using var serialized = JsonDocument.Parse(Serializer.SerializeToUtf8Bytes(review));
        Assert.Equal("CaseActionRequestReviewed", serialized.RootElement.GetProperty("eventType").GetString());
        Assert.Equal(outcome.ToString(), serialized.RootElement.GetProperty("payload").GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, serialized.RootElement.GetProperty("payload").GetProperty("linkedAcceptedEventId").ValueKind);
        AssertReviewLinkConditionFromSchema(serialized.RootElement.GetProperty("payload"));
    }

    [Fact]
    public void ApprovedReview_RequiresAndSerializesLinkedAcceptedEvent()
    {
        var report = new ReportFacts(CaseKind.Human, Sector.HumanHealth, "reported details", []);
        var initialEvents = CaseAggregate.Create(
            new AcceptCaseCommand(InitialCommandId, CaseId, report, "officer-1", "workspace-1", OccurredAtUtc, RecordedAtUtc),
            new ActorCapabilities("officer-1", CaseCapability.AcceptReport));
        var aggregate = CaseAggregate.Replay(initialEvents);
        var requestId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var requestEvents = aggregate.Decide(
            new RequestCaseActionCommand(Guid.Parse("20000000-0000-0000-0000-000000000002"), aggregate.AggregateVersion,
                requestId, CaseAction.Reassign, "request transfer", [], "worker-2", null, RecordedAtUtc),
            new ActorCapabilities("worker-1", CaseCapability.RequestProtectedAction));
        var requested = CaseAggregate.Replay(initialEvents.Concat(requestEvents));
        var reviewEvents = requested.Decide(
            new ReviewCaseActionRequestCommand(Guid.Parse("20000000-0000-0000-0000-000000000003"), requested.AggregateVersion,
                requestId, CaseActionReviewOutcome.Approved, null, "authorized transfer", null, RecordedAtUtc),
            new ActorCapabilities("officer-1", CaseCapability.ReviewProtectedAction | CaseCapability.AssignCase));
        var assignment = Assert.IsType<CaseAssigned>(reviewEvents[0]);
        var review = Assert.IsType<CaseActionRequestReviewed>(reviewEvents[1]);

        Assert.Equal(requestId, review.RequestId);
        Assert.Equal(assignment.EventId, review.LinkedAcceptedEventId);
        using var serialized = JsonDocument.Parse(Serializer.SerializeToUtf8Bytes(review));
        var payload = serialized.RootElement.GetProperty("payload");
        Assert.Equal(requestId, payload.GetProperty("requestId").GetGuid());
        Assert.Equal(assignment.EventId, payload.GetProperty("linkedAcceptedEventId").GetGuid());
        AssertReviewLinkConditionFromSchema(payload);
    }

    [Fact]
    public void SchemaConditional_RejectsApprovedReviewWithoutLinkAndDeniedReviewWithLink()
    {
        var invalidReviews = new[]
        {
            new CaseActionRequestReviewed(CaseId, 5, 1, Guid.NewGuid(), "officer-1", null, RecordedAtUtc,
                Guid.NewGuid(), CaseActionReviewOutcome.Approved, null, "missing accepted event"),
            new CaseActionRequestReviewed(CaseId, 4, 1, Guid.NewGuid(), "officer-1", null, RecordedAtUtc,
                Guid.NewGuid(), CaseActionReviewOutcome.Denied, Guid.NewGuid(), "denied with invalid link")
        };

        foreach (var review in invalidReviews)
        {
            Assert.Throws<InvalidDataException>(() => Serializer.SerializeToUtf8Bytes(review));
        }
    }

    private static string FixtureDirectory => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../contracts/fixtures/case-events"));

    private static void AssertReviewLinkConditionFromSchema(JsonElement payload)
    {
        var schemaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../contracts/events/case-event.v1.schema.json"));
        using var schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
        var conditions = schema.RootElement.GetProperty("$defs").GetProperty("actionReview").GetProperty("allOf");
        var outcome = payload.GetProperty("outcome").GetString();
        var link = payload.GetProperty("linkedAcceptedEventId");

        foreach (var condition in conditions.EnumerateArray())
        {
            var outcomeSchema = condition.GetProperty("if").GetProperty("properties").GetProperty("outcome");
            var matches = outcomeSchema.TryGetProperty("const", out var expected)
                ? expected.GetString() == outcome
                : outcomeSchema.GetProperty("enum").EnumerateArray().Any(value => value.GetString() == outcome);
            if (!matches) continue;

            var expectedLinkType = condition.GetProperty("then").GetProperty("properties")
                .GetProperty("linkedAcceptedEventId").GetProperty("type").GetString();
            Assert.Equal(expectedLinkType == "null" ? JsonValueKind.Null : JsonValueKind.String, link.ValueKind);
            if (expectedLinkType == "string") Assert.True(Guid.TryParse(link.GetString(), out _));
            return;
        }

        throw new Xunit.Sdk.XunitException($"No linked-event rule in the schema matches outcome '{outcome}'.");
    }
}
