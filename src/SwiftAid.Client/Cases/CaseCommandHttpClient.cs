using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Client.Cases;

/// <summary>Staff case-command client over the authenticated Gateway API.</summary>
/// <remarks>
/// Sends command routing fields only; authorization always derives from the
/// server-authenticated bearer principal. Bearer calls never carry an
/// antiforgery token: the endpoint requires antiforgery only when a valid
/// browser cookie is also present, which native clients do not use.
/// When the caller does not supply a usable concurrency hint, the client
/// resolves the authoritative aggregate version from the timeline first,
/// mirroring the server-side command client. Denial and absence still share
/// one outcome so callers cannot probe for case existence.
/// </remarks>
public sealed class CaseCommandHttpClient(HttpClient httpClient) : ICaseCommandClient
{
    private const int WireCommandSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CaseCommandResult> SendAsync(
        Guid caseId,
        CaseCommandInput input,
        CancellationToken cancellationToken = default)
    {
        var commandId = input?.CommandId ?? Guid.NewGuid();
        if (input is null || caseId == Guid.Empty || string.IsNullOrWhiteSpace(input.CommandType))
        {
            return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
        }

        if (commandId == Guid.Empty)
        {
            return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
        }

        if (httpClient.BaseAddress is null)
        {
            return new CaseCommandResult(CaseCommandStatus.Unavailable, commandId, [], null, null);
        }

        try
        {
            long expectedVersion;
            if (input.ExpectedVersion is >= 2)
            {
                expectedVersion = input.ExpectedVersion.Value;
            }
            else
            {
                var resolved = await ResolveExpectedVersionAsync(caseId, cancellationToken).ConfigureAwait(false);
                if (resolved is null)
                {
                    return new CaseCommandResult(CaseCommandStatus.Denied, commandId, [], null, null);
                }

                if (resolved.Value < 0)
                {
                    return new CaseCommandResult(CaseCommandStatus.Unavailable, commandId, [], null, null);
                }

                expectedVersion = resolved.Value;
            }

            using var response = await httpClient.PostAsJsonAsync(
                $"api/v1/cases/{caseId:D}/commands",
                new CaseCommandWireRequest(
                    WireCommandSchemaVersion,
                    input.CommandType,
                    commandId,
                    expectedVersion,
                    null,
                    input.Reason,
                    input.ProposedOwnerId,
                    input.RequestId,
                    input.Outcome,
                    input.Action,
                    input.ObservationId,
                    input.Narrative,
                    input.Source,
                    input.FollowUpId,
                    input.Purpose,
                    input.AssigneeActorId,
                    input.DueAtUtc,
                    input.OutcomeName,
                    input.OutcomeNotes,
                    input.NewDueAtUtc,
                    input.EscalationId,
                    input.RecipientRoute,
                    input.Resolution,
                    input.ActionSummary,
                    input.EvidenceEventIds,
                    input.AssignmentVersion, input.GrantId, input.CollaboratorActorId, input.CollaborationActions, input.ExpiresAtUtc,
                    input.AssistanceId, input.AssistanceNeed, input.ResponsibleActorId, input.AssistanceStatus?.ToString(),
                    input.AssistanceNote, input.ReceiptMethod, input.ReceiptContext,
                    input.ActionOwnerKind, input.ActionId),
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => await MapAcceptedAsync(response, commandId, cancellationToken).ConfigureAwait(false),
                HttpStatusCode.NotFound =>
                    new CaseCommandResult(CaseCommandStatus.Denied, commandId, [], null, null),
                HttpStatusCode.Conflict =>
                    new CaseCommandResult(CaseCommandStatus.Conflict, commandId, [], null, null),
                HttpStatusCode.BadRequest =>
                    new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null),
                _ => new CaseCommandResult(CaseCommandStatus.Unavailable, commandId, [], null, null),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new CaseCommandResult(CaseCommandStatus.Unavailable, commandId, [], null, null);
        }
    }

    /// <summary>
    /// Resolves the authoritative aggregate version from the timeline.
    /// Returns the version, null when denied-or-absent, or -1 when unavailable.
    /// </summary>
    private async Task<long?> ResolveExpectedVersionAsync(Guid caseId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/cases/{caseId:D}/timeline", cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound
            or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return -1;
        }

        try
        {
            var timeline = await response.Content
                .ReadFromJsonAsync<CaseTimeline>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (timeline?.Header is null || timeline.Header.CaseId != caseId)
            {
                return -1;
            }

            return timeline.Header.AggregateVersion;
        }
        catch (JsonException)
        {
            return -1;
        }
    }

    private static async Task<CaseCommandResult> MapAcceptedAsync(
        HttpResponseMessage response,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        try
        {
            var receipt = await response.Content
                .ReadFromJsonAsync<CaseCommandWireResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (receipt is null || receipt.CaseId == Guid.Empty || receipt.CommandId == Guid.Empty)
            {
                return new CaseCommandResult(CaseCommandStatus.Unavailable, commandId, [], null, null);
            }

            // The wire carries no Accepted/Replayed discriminator; a fresh idempotency
            // key per send means a 200 receipt is recorded work either way.
            return new CaseCommandResult(
                CaseCommandStatus.Accepted,
                receipt.CommandId,
                receipt.EventIds ?? [],
                receipt.AggregateVersion,
                receipt.ReportRevision);
        }
        catch (JsonException)
        {
            return new CaseCommandResult(CaseCommandStatus.Unavailable, commandId, [], null, null);
        }
    }

    /// <summary>Version-one wire request. Property names match the endpoint contract.</summary>
    private sealed record CaseCommandWireRequest(
        int SchemaVersion,
        string? CommandType,
        Guid CommandId,
        long ExpectedVersion,
        DateTimeOffset? OccurredAtUtc,
        string? Reason,
        string? ProposedOwnerId,
        Guid? RequestId,
        string? Outcome,
        string? Action,
        Guid? ObservationId,
        string? Narrative,
        string? Source,
        Guid? FollowUpId,
        string? Purpose,
        string? AssigneeActorId,
        DateTimeOffset? DueAtUtc,
        string? OutcomeName,
        string? OutcomeNotes,
        DateTimeOffset? NewDueAtUtc,
        Guid? EscalationId,
        string? RecipientRoute,
        string? Resolution,
        string? ActionSummary,
        IReadOnlyList<string>? EvidenceEventIds,
        long? AssignmentVersion,
        Guid? GrantId,
        string? CollaboratorActorId,
        CaseCollaborationActions? CollaborationActions,
        DateTimeOffset? ExpiresAtUtc,
        Guid? AssistanceId,
        string? AssistanceNeed,
        string? ResponsibleActorId,
        string? AssistanceStatus,
        string? AssistanceNote,
        string? ReceiptMethod,
        string? ReceiptContext,
        SwiftAid.Contracts.Commands.ActionOwnerKind? ActionOwnerKind,
        Guid? ActionId);

    /// <summary>Accepted case-command receipt as returned by the endpoint.</summary>
    private sealed record CaseCommandWireResponse(
        Guid CaseId,
        Guid CommandId,
        IReadOnlyList<Guid>? EventIds,
        long? AggregateVersion,
        long? ReportRevision);
}
