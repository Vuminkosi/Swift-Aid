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

public sealed class CaseObservationGatewayClient(AuthenticationStateProvider authentication, IServiceScopeFactory scopes,
    ILogger<CaseObservationGatewayClient> logger) : ICaseObservationClient
{
    public async Task<UiCapabilityResult<CaseObservationSnapshot>> GetAsync(Guid caseId, PageRequest page,
        CancellationToken cancellationToken = default)
    {
        if (caseId == Guid.Empty || !page.IsValid || page.PageNumber > 100_000)
            return Result<CaseObservationSnapshot>(UiCapabilityResultCode.InvalidRequest);
        try
        {
            var data = await ReadAsync(caseId, cancellationToken);
            if (data is null) return Result<CaseObservationSnapshot>(UiCapabilityResultCode.Denied);
            var offset = (page.PageNumber - 1) * page.PageSize;
            return Result(UiCapabilityResultCode.Success, new CaseObservationSnapshot(caseId, data.Value.Version,
                new Page<CaseObservationRecord>(data.Value.Rows.Skip(offset).Take(page.PageSize).ToArray(),
                    page.PageNumber, page.PageSize, data.Value.Rows.Length, offset + page.PageSize < data.Value.Rows.Length)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return Result<CaseObservationSnapshot>(UiCapabilityResultCode.Denied); }
        catch (Exception error)
        {
            logger.LogWarning(error, "Case observations could not be read.");
            return Result<CaseObservationSnapshot>(UiCapabilityResultCode.Unavailable);
        }
    }

    public async Task<UiCapabilityResult<CaseObservationWriteResult>> RecordAsync(CaseObservationCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!command.IsValid || command.Category == CaseObservationCategory.Unknown || command.EvidenceKeys.Count != 0)
            return Result<CaseObservationWriteResult>(UiCapabilityResultCode.InvalidRequest);
        try
        {
            var actor = ActorContext.FromValidatedPrincipal((await authentication.GetAuthenticationStateAsync()).User);
            await using var scope = scopes.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<ICaseCommandProcessor>().ProcessAsync(actor,
                command.CaseId, 1, new RecordObservationCommand(command.SubmissionId, command.ExpectedVersion,
                    command.SubmissionId, command.Observation, command.SourceLabel, "Staff-recorded observation",
                    command.OccurredAtUtc, DateTimeOffset.UtcNow, command.Category.ToString()), cancellationToken);
            if (result.Status is not (CaseCommandProcessingStatus.Accepted or CaseCommandProcessingStatus.Replayed))
                return Result<CaseObservationWriteResult>(result.Status switch
                {
                    CaseCommandProcessingStatus.Conflict => UiCapabilityResultCode.Conflict,
                    CaseCommandProcessingStatus.Denied => UiCapabilityResultCode.Denied,
                    CaseCommandProcessingStatus.Invalid => UiCapabilityResultCode.InvalidRequest,
                    _ => UiCapabilityResultCode.Unavailable
                });
            var data = await ReadAsync(command.CaseId, cancellationToken);
            var saved = data?.Rows.SingleOrDefault(row => row.ObservationKey == command.SubmissionId.ToString("D"));
            return saved is null ? Result<CaseObservationWriteResult>(UiCapabilityResultCode.Unavailable)
                : Result(UiCapabilityResultCode.Success, new CaseObservationWriteResult(command.CaseId,
                    command.SubmissionId, saved.ObservationKey, saved.AggregateVersion, saved.RecordedAtUtc, saved.OccurredAtUtc));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return Result<CaseObservationWriteResult>(UiCapabilityResultCode.Denied); }
        catch (Exception error)
        {
            logger.LogWarning(error, "Case observation could not be recorded.");
            return Result<CaseObservationWriteResult>(UiCapabilityResultCode.Unavailable);
        }
    }

    private async Task<(long Version, CaseObservationRecord[] Rows)?> ReadAsync(Guid caseId, CancellationToken ct)
    {
        var actor = ActorContext.FromValidatedPrincipal((await authentication.GetAuthenticationStateAsync()).User);
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        ICaseCommandLease lease;
        try { lease = await services.GetRequiredService<ICaseCommandUnitOfWork>()
            .BeginCaseAsync(actor, caseId, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct); }
        catch (KeyNotFoundException) { return null; }
        await using (lease)
        {
            if (!(await services.GetRequiredService<ICaseAccessPolicy>().AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, ct)).IsAllowed)
                return null;
            var aggregate = await services.GetRequiredService<ICaseCommandPersistence>().ReplayCaseAsync(lease, ct);
            var observations = aggregate.Events.OfType<CaseObservationRecorded>().OrderByDescending(row => row.AggregateVersion).ToArray();
            var actors = observations.Select(row => row.ActorId).Distinct().ToArray();
            var labels = await services.GetRequiredService<SwiftAidDbContext>().UserProfiles.AsNoTracking()
                .Where(row => actors.Contains(row.UserId)).ToDictionaryAsync(row => row.UserId, row => row.DisplayName, ct);
            var rows = observations.Select(row => new CaseObservationRecord(row.ObservationId.ToString("D"),
                Enum.TryParse<CaseObservationCategory>(row.Category, out var category) ? category : CaseObservationCategory.Unknown,
                row.Narrative, row.Source, [], row.OccurredAtUtc, row.RecordedAtUtc,
                labels.TryGetValue(row.ActorId, out var label) && !string.IsNullOrWhiteSpace(label)
                    && label.Length <= 120 && !label.Any(char.IsControl) ? label : "Staff member", row.AggregateVersion)).ToArray();
            await lease.CommitAsync(ct);
            return (aggregate.AggregateVersion, rows);
        }
    }

    private static UiCapabilityResult<T> Result<T>(UiCapabilityResultCode code, T? value = default) =>
        new(code, value, new("case-observations", UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService,
            DateTimeOffset.UtcNow, code == UiCapabilityResultCode.Success));
}
