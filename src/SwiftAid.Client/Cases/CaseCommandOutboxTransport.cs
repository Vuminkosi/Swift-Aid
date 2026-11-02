using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Client.Outbox;
using SwiftAid.Client.Sync;

namespace SwiftAid.Client.Cases;

/// <summary>
/// Sends frozen outbox commands to the version-one case-command endpoint using the
/// stable outbox command id as the idempotency key, so reconnect retries never record
/// a duplicate business effect. The wire contract is unchanged: only routing fields
/// travel; authorization derives from the server-authenticated principal.
/// </summary>
public sealed class CaseCommandOutboxTransport(HttpClient httpClient) : ICaseCommandTransport
{
    private const int WireCommandSchemaVersion = 1;
    private const long MinimumWireExpectedVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CaseCommandDeliveryResult> SendAsync(
        OutboundCaseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ExpectedAggregateVersion < MinimumWireExpectedVersion)
        {
            return new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.ValidationRejected);
        }

        CaseCommandIntent intent;
        try
        {
            intent = CaseCommandIntentCodec.Decode(command.Payload);
        }
        catch (InvalidOperationException)
        {
            return new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.ValidationRejected);
        }

        if (httpClient.BaseAddress is null)
        {
            return new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.TemporarilyUnavailable);
        }

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"api/v1/cases/{command.CaseId:D}/commands",
                new
                {
                    schemaVersion = WireCommandSchemaVersion,
                    commandType = intent.CommandType,
                    commandId = command.CommandId,
                    expectedVersion = command.ExpectedAggregateVersion,
                    occurredAtUtc = (DateTimeOffset?)null,
                    reason = intent.Reason,
                    proposedOwnerId = intent.ProposedOwnerId,
                    requestId = intent.RequestId,
                    outcome = intent.Outcome,
                    action = intent.Action,
                    observationId = intent.ObservationId,
                    narrative = intent.Narrative,
                    source = intent.Source,
                    followUpId = intent.FollowUpId,
                    purpose = intent.Purpose,
                    assigneeActorId = intent.AssigneeActorId,
                    dueAtUtc = intent.DueAtUtc,
                    outcomeName = intent.OutcomeName,
                    outcomeNotes = intent.OutcomeNotes,
                    newDueAtUtc = intent.NewDueAtUtc,
                    escalationId = intent.EscalationId,
                    recipientRoute = intent.RecipientRoute,
                    resolution = intent.Resolution,
                    actionSummary = intent.ActionSummary,
                    evidenceEventIds = intent.EvidenceEventIds,
                    assignmentVersion = intent.AssignmentVersion,
                    grantId = intent.GrantId,
                    collaboratorActorId = intent.CollaboratorActorId,
                    collaborationActions = intent.CollaborationActions,
                    expiresAtUtc = intent.ExpiresAtUtc,
                },
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => await MapAcceptedAsync(command, response, cancellationToken).ConfigureAwait(false),
                HttpStatusCode.NotFound => new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.AuthorizationDenied),
                HttpStatusCode.Unauthorized => new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.AuthorizationDenied),
                HttpStatusCode.Forbidden => new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.AuthorizationDenied),
                HttpStatusCode.Conflict => await MapConflictAsync(command, cancellationToken).ConfigureAwait(false),
                HttpStatusCode.BadRequest => new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.ValidationRejected),
                _ => new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.TemporarilyUnavailable),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Transport-level failure (offline, timeout, TLS): keep the intent queued for retry.
            return new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.TemporarilyUnavailable);
        }
    }

    private async Task<CaseCommandDeliveryResult> MapAcceptedAsync(
        OutboundCaseCommand command,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var receipt = await response.Content
                .ReadFromJsonAsync<CaseCommandReceipt>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (receipt is null || receipt.CaseId != command.CaseId ||
                receipt.CommandId != command.CommandId ||
                receipt.AggregateVersion <= 0)
            {
                return new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.TemporarilyUnavailable);
            }

            // A 200 for a replayed idempotency key carries the original receipt: accepted work either way.
            return new CaseCommandDeliveryResult(
                CaseCommandDeliveryStatus.Accepted,
                AcceptedAggregateVersion: receipt.AggregateVersion);
        }
        catch (JsonException)
        {
            return new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.TemporarilyUnavailable);
        }
    }

    private async Task<CaseCommandDeliveryResult> MapConflictAsync(
        OutboundCaseCommand command,
        CancellationToken cancellationToken)
    {
        // A conflict needs the current authorized version plus a snapshot for the
        // reconcile path. Without both, retry later instead of blocking on partial data.
        try
        {
            using var response = await httpClient.GetAsync(
                $"api/v1/cases/{command.CaseId:D}/timeline", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.TemporarilyUnavailable);
            }

            var timeline = await response.Content
                .ReadFromJsonAsync<SwiftAid.Contracts.Cases.CaseTimeline>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (timeline?.Header is null || timeline.Header.CaseId != command.CaseId)
            {
                return new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.TemporarilyUnavailable);
            }

            var snapshot = JsonSerializer.SerializeToUtf8Bytes(timeline.Header, JsonOptions);
            return new CaseCommandDeliveryResult(
                CaseCommandDeliveryStatus.Conflict,
                LatestAggregateVersion: timeline.Header.AggregateVersion,
                LatestAuthorizedSnapshot: snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.TemporarilyUnavailable);
        }
    }

    private sealed record CaseCommandReceipt(
        Guid CaseId,
        Guid CommandId,
        IReadOnlyList<Guid>? EventIds,
        long? AggregateVersion,
        long? ReportRevision);
}

/// <summary>
/// Binds the exact first-send intent bytes stored at enqueue time. The expected
/// aggregate version travels as a separate wire field, so retries always send the
/// same frozen bytes and hash while the server dedupes on the stable command id.
/// </summary>
public sealed class CaseCommandPayloadBinder : ICaseCommandPayloadBinder
{
    public Task<byte[]> BindAsync(
        LocalCommandProposal proposal,
        long expectedAggregateVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(proposal.ProposalPayload.ToArray());
    }
}
