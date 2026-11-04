using System.Collections.Immutable;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Gateway.Cases;

public sealed class CaseDiscussionGatewayClient(AuthenticationStateProvider authentication, IServiceScopeFactory scopes,
    ILogger<CaseDiscussionGatewayClient> logger) : ICaseDiscussionClient, ICaseDiscussionCommandClient
{
    public Task<UiCapabilityResult<CaseDiscussionSnapshot>> GetAsync(Guid caseId, CaseDiscussionQuery query,
        CancellationToken cancellationToken = default) => !query.IsValid
        ? Task.FromResult(Result<CaseDiscussionSnapshot>(UiCapabilityResultCode.InvalidRequest))
        : ReadAsync(caseId, async (aggregate, db, ct) =>
        {
            var events = aggregate.Events.OfType<CaseDiscussionPosted>().OrderByDescending(item => item.AggregateVersion).ToArray();
            var offset = (query.Page.PageNumber - 1) * query.Page.PageSize;
            var page = events.Skip(offset).Take(query.Page.PageSize).ToArray();
            var authors = page.Select(item => item.ActorId).Distinct().ToArray();
            var labels = await db.UserProfiles.AsNoTracking().Where(profile => authors.Contains(profile.UserId))
                .ToDictionaryAsync(profile => profile.UserId, profile => profile.DisplayName, ct);
            return new CaseDiscussionSnapshot(caseId, aggregate.AggregateVersion,
                new Page<CaseDiscussionMessage>(page.Select(item => new CaseDiscussionMessage(item.CommandId.ToString("D"),
                    Label(labels.GetValueOrDefault(item.ActorId)), item.Message, item.RecordedAtUtc,
                    item.MentionReferences, item.EvidenceKeys)).ToArray(), query.Page.PageNumber, query.Page.PageSize,
                    events.Length, offset + page.Length < events.Length));
        }, cancellationToken);

    public Task<UiCapabilityResult<IReadOnlyList<CaseDiscussionMentionTarget>>> GetMentionTargetsAsync(Guid caseId,
        CancellationToken cancellationToken = default) => ReadAsync<IReadOnlyList<CaseDiscussionMentionTarget>>(caseId,
        async (aggregate, db, ct) =>
        {
            if (aggregate.AccountableOwnerId is not { } owner) return [];
            var label = await db.UserProfiles.AsNoTracking().Where(profile => profile.UserId == owner)
                .Select(profile => profile.DisplayName).SingleOrDefaultAsync(ct);
            return [new(owner, Label(label))];
        }, cancellationToken);

    public async Task<UiCapabilityResult<CaseDiscussionPostResult>> AppendAsync(CaseDiscussionPostCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!command.IsValid) return Result<CaseDiscussionPostResult>(UiCapabilityResultCode.InvalidRequest);
        try
        {
            var actor = ActorContext.FromValidatedPrincipal((await authentication.GetAuthenticationStateAsync()).User);
            await using var scope = scopes.CreateAsyncScope();
            var processed = await scope.ServiceProvider.GetRequiredService<ICaseCommandProcessor>().ProcessAsync(actor,
                command.CaseId, 1, new PostCaseDiscussionCommand(command.SubmissionId, command.ExpectedVersion,
                    command.GetCanonicalMessage(), command.GetCanonicalMentionReferences(), command.GetCanonicalEvidenceKeys(),
                    DateTimeOffset.UtcNow), cancellationToken);
            if (processed.Status is not (CaseCommandProcessingStatus.Accepted or CaseCommandProcessingStatus.Replayed))
                return Result<CaseDiscussionPostResult>(processed.Status switch
                {
                    CaseCommandProcessingStatus.Conflict => UiCapabilityResultCode.Conflict,
                    CaseCommandProcessingStatus.Denied => UiCapabilityResultCode.Denied,
                    CaseCommandProcessingStatus.Invalid => UiCapabilityResultCode.InvalidRequest,
                    _ => UiCapabilityResultCode.Unavailable
                });
            return await ReadAsync(command.CaseId, (aggregate, _, _) =>
            {
                var saved = aggregate.Events.OfType<CaseDiscussionPosted>().Single(item => item.CommandId == command.SubmissionId);
                return Task.FromResult(new CaseDiscussionPostResult(command.CaseId, command.SubmissionId,
                    saved.CommandId.ToString("D"), saved.AggregateVersion, saved.RecordedAtUtc));
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return Result<CaseDiscussionPostResult>(UiCapabilityResultCode.Denied); }
        catch (Exception error)
        {
            logger.LogWarning(error, "Case discussion could not be posted.");
            return Result<CaseDiscussionPostResult>(UiCapabilityResultCode.Unavailable);
        }
    }

    private async Task<UiCapabilityResult<T>> ReadAsync<T>(Guid caseId,
        Func<CaseAggregate, SwiftAidDbContext, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        if (caseId == Guid.Empty) return Result<T>(UiCapabilityResultCode.InvalidRequest);
        try
        {
            var actor = ActorContext.FromValidatedPrincipal((await authentication.GetAuthenticationStateAsync()).User);
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            await using var lease = await services.GetRequiredService<ICaseCommandUnitOfWork>()
                .BeginCaseAsync(actor, caseId, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct);
            if (!(await services.GetRequiredService<ICaseAccessPolicy>().AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, ct)).IsAllowed)
                return Result<T>(UiCapabilityResultCode.Denied);
            var aggregate = await services.GetRequiredService<ICaseCommandPersistence>().ReplayCaseAsync(lease, ct);
            var value = await read(aggregate, services.GetRequiredService<SwiftAidDbContext>(), ct);
            await lease.CommitAsync(ct);
            return Result(UiCapabilityResultCode.Success, value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is UnauthorizedAccessException or KeyNotFoundException)
        { return Result<T>(UiCapabilityResultCode.Denied); }
        catch (Exception error)
        {
            logger.LogWarning(error, "Case discussion could not be read.");
            return Result<T>(UiCapabilityResultCode.Unavailable);
        }
    }

    private static string Label(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 120 && !value.Any(char.IsControl)
        ? value : "Staff member";

    private static UiCapabilityResult<T> Result<T>(UiCapabilityResultCode code, T? value = default) =>
        new(code, value, new("case-discussion", UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService,
            DateTimeOffset.UtcNow, code == UiCapabilityResultCode.Success));
}