using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Gateway.Cases;

/// <summary>
/// Server-side command client for Blazor Server: resolves the current staff
/// principal per call and invokes the command processor directly. All data rules
/// (authorization, idempotency, optimistic concurrency) are enforced by the
/// processor, not by this adapter. The expected version always comes from the
/// authoritative timeline header. Deployments without the case command pipeline
/// fail closed with the same unavailable outcome.
/// </summary>
public sealed class CaseCommandGatewayClient(
    AuthenticationStateProvider authenticationStateProvider,
    IServiceProvider serviceProvider) : ICaseCommandClient
{
    private const int WireCommandSchemaVersion = 1;

    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
    private readonly IServiceProvider _serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    public async Task<CaseCommandResult> SendAsync(
        Guid caseId,
        CaseCommandInput input,
        CancellationToken cancellationToken = default)
    {
        var actor = await GetActorAsync().ConfigureAwait(false);
        if (actor is null || caseId == Guid.Empty || input is null)
        {
            return Unavailable(Guid.Empty);
        }

        var processor = _serviceProvider.GetService<ICaseCommandProcessor>();
        var timeline = _serviceProvider.GetService<ICaseTimelineQuery>();
        if (processor is null || timeline is null)
        {
            return Unavailable(Guid.Empty);
        }

        CaseTimelineResult timelineResult;
        try
        {
            timelineResult = await timeline.GetAsync(actor, caseId, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Unavailable(Guid.Empty);
        }

        // Denial and absence share one outcome so callers cannot probe for case existence.
        if (timelineResult.Access != CaseTimelineAccess.Found || timelineResult.Timeline is null)
        {
            return new CaseCommandResult(CaseCommandStatus.Denied, Guid.Empty, [], null, null);
        }

        CaseCommand command;
        var commandId = Guid.NewGuid();
        var expectedVersion = timelineResult.Timeline.Header.AggregateVersion;
        try
        {
            command = MapCommand(input, commandId, expectedVersion);
        }
        catch (ArgumentException)
        {
            return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
        }

        try
        {
            var result = await processor.ProcessAsync(
                actor, caseId, WireCommandSchemaVersion, command, cancellationToken).ConfigureAwait(false);
            return MapResult(result, commandId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable(commandId);
        }
    }

    private static CaseCommand MapCommand(CaseCommandInput input, Guid commandId, long expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(input.CommandType))
        {
            throw new ArgumentException("A case command type is required.", nameof(input));
        }

        // The processor replaces this placeholder with the database clock; it is never trusted.
        var recordedAt = DateTimeOffset.UtcNow;

        return input.CommandType switch
        {
            "CorrectReport" => new CorrectReportCommand(
                commandId,
                expectedVersion,
                RequireReason(input),
                RequireReason(input),
                null,
                recordedAt),
            "RequestProtectedAction" => new RequestCaseActionCommand(
                commandId,
                expectedVersion,
                input.RequestId ?? commandId,
                RequireAction(input.Action),
                RequireReason(input),
                [],
                RequireActionOwner(input),
                null,
                recordedAt),
            "ReviewProtectedAction" => new ReviewCaseActionRequestCommand(
                commandId,
                expectedVersion,
                input.RequestId ?? commandId,
                RequireOutcome(input.Outcome),
                RequireOptionalOwnerId(input.ProposedOwnerId),
                RequireReason(input),
                null,
                recordedAt),
            "Resolve" => new ResolveCaseCommand(
                commandId,
                expectedVersion,
                RequireReason(input),
                null,
                recordedAt),
            "Reopen" => new ReopenCaseCommand(
                commandId,
                expectedVersion,
                RequireReason(input),
                RequireOwnerId(input.ProposedOwnerId),
                null,
                recordedAt),
            _ => throw new ArgumentException("The case command type is not supported.", nameof(input)),
        };
    }

    private static CaseCommandResult MapResult(CaseCommandProcessingResult result, Guid commandId)
    {
        var status = result.Status switch
        {
            CaseCommandProcessingStatus.Accepted => CaseCommandStatus.Accepted,
            CaseCommandProcessingStatus.Replayed => CaseCommandStatus.Replayed,
            CaseCommandProcessingStatus.Conflict => CaseCommandStatus.Conflict,
            CaseCommandProcessingStatus.Denied => CaseCommandStatus.Denied,
            CaseCommandProcessingStatus.Invalid => CaseCommandStatus.Invalid,
            _ => CaseCommandStatus.Unavailable,
        };

        IReadOnlyList<Guid> eventIds = result.EventIds.IsDefault
            ? []
            : result.EventIds.ToArray();

        var id = result.CommandId != Guid.Empty ? result.CommandId : commandId;
        return new CaseCommandResult(status, id, eventIds, result.AggregateVersion, result.ReportRevision);
    }

    private static CaseCommandResult Unavailable(Guid commandId) =>
        new(CaseCommandStatus.Unavailable, commandId, [], null, null);

    private static string RequireReason(CaseCommandInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Reason))
        {
            throw new ArgumentException("A case command reason is required.", nameof(input));
        }

        return input.Reason;
    }

    private static string RequireOwnerId(string? proposedOwnerId)
    {
        if (string.IsNullOrWhiteSpace(proposedOwnerId) ||
            !string.Equals(proposedOwnerId, proposedOwnerId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical accountable owner identifier is required.", nameof(proposedOwnerId));
        }

        return proposedOwnerId;
    }

    private static string? RequireOptionalOwnerId(string? proposedOwnerId)
    {
        if (proposedOwnerId is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(proposedOwnerId) ||
            !string.Equals(proposedOwnerId, proposedOwnerId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The proposed owner identifier is invalid.", nameof(proposedOwnerId));
        }

        return proposedOwnerId;
    }

    private static CaseAction RequireAction(string? action)
    {
        if (!Enum.TryParse<CaseAction>(action, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(parsed))
        {
            throw new ArgumentException("The protected action is not supported.", nameof(action));
        }

        return parsed;
    }

    private static string? RequireActionOwner(CaseCommandInput input)
    {
        var action = RequireAction(input.Action);
        if (action is CaseAction.Reassign or CaseAction.Assist)
        {
            return RequireOwnerId(input.ProposedOwnerId);
        }

        return RequireOptionalOwnerId(input.ProposedOwnerId);
    }

    private static CaseActionReviewOutcome RequireOutcome(string? outcome)
    {
        if (!Enum.TryParse<CaseActionReviewOutcome>(outcome, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(parsed))
        {
            throw new ArgumentException("The protected-action review outcome is not supported.", nameof(outcome));
        }

        return parsed;
    }

    private async Task<ActorContext?> GetActorAsync()
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = state.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        try
        {
            return ActorContext.FromValidatedPrincipal(principal);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
