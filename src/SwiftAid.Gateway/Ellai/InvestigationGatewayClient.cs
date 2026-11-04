using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Sync;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Investigation projections over the existing durable Ellai turn, policy and approval pipeline.</summary>
public sealed class InvestigationGatewayClient(AuthenticationStateProvider authentication, IServiceScopeFactory scopes,
    IDataProtectionProvider protection, ILogger<InvestigationGatewayClient> logger) : IAiInvestigationClient
{
    private sealed record ReviewIntent(string ProposalId, bool Approve, string Reason, string ActorId, DateTimeOffset RecordedAtUtc);
    private sealed record StoredRequest(string Title, string Objective, string ScopeHash, IReadOnlyList<ReviewIntent>? Reviews = null);
    private sealed record Context(IServiceProvider Services, SwiftAidDbContext Db, ActorContext Actor, ScopeManifest Scope, EllaiReviewGatewayClient Review);

    public Task<UiCapabilityResult<Page<InvestigationWorkItem>>> ListAsync(PageRequest page, CancellationToken cancellationToken = default) =>
        RunAsync(async context =>
        {
            if (!page.IsValid || page.PageNumber > 100000) throw new ArgumentException("Invalid page.");
            var query = context.Db.Set<AgentInvestigationEntity>().AsNoTracking().Where(row => row.OwnerActorId == context.Actor.ActorId);
            var count = await query.CountAsync(cancellationToken);
            var rows = await query.OrderByDescending(row => row.CreatedAtUtc).ThenBy(row => row.SessionId)
                .Skip((page.PageNumber - 1) * page.PageSize).Take(page.PageSize).ToArrayAsync(cancellationToken);
            var items = new List<InvestigationWorkItem>();
            foreach (var row in rows)
            {
                var request = Unprotect(row);
                if (request.ScopeHash != ScopeHash(context.Scope)) continue;
                var snapshot = await SnapshotAsync(context, row, request, cancellationToken);
                items.Add(new(snapshot.Id, snapshot.Title, snapshot.Objective, snapshot.Status, snapshot.Version, snapshot.UpdatedAtUtc!.Value, false));
            }
            return new Page<InvestigationWorkItem>(items, page.PageNumber, page.PageSize, count, page.PageNumber * page.PageSize < count);
        }, cancellationToken);

    public Task<UiCapabilityResult<InvestigationSnapshot>> GetAsync(string investigationId, CancellationToken cancellationToken = default) =>
        RunAsync(async context =>
        {
            var row = await OwnedAsync(context, investigationId, cancellationToken);
            return await SnapshotAsync(context, row, Unprotect(row), cancellationToken);
        }, cancellationToken);

    public Task<UiCapabilityResult<InvestigationSnapshot>> StartAsync(InvestigationRequest request, CancellationToken cancellationToken = default) =>
        RunAsync(async context =>
        {
            Text(request.Title, 160); Text(request.Objective, 4000);
            var started = await context.Review.StartSessionAsync(EllaiMode.Ask, cancellationToken);
            if (started.Status != EllaiClientOperationStatus.Accepted || started.Session is null) throw new InvalidOperationException("Session was not accepted.");
            var session = await context.Db.EllaiSessions.SingleAsync(row => row.SessionId == started.Session.SessionId, cancellationToken);
            var row = new AgentInvestigationEntity { SessionId = session.SessionId, OwnerActorId = context.Actor.ActorId,
                CreatedAtUtc = DateTimeOffset.UtcNow, ProtectedRequest = Protect(session.SessionId,
                    new(request.Title.Trim(), request.Objective.Trim(), ScopeHash(context.Scope))) };
            context.Db.Add(row);
            session.ActiveInvestigationId = session.SessionId;
            session.Version++;
            await context.Db.SaveChangesAsync(cancellationToken);
            await SubmitAsync(context, row, session.Version, $"Investigate these factual and administrative records. Objective: {request.Objective.Trim()}", "start", cancellationToken);
            return await SnapshotAsync(context, row, Unprotect(row), cancellationToken);
        }, cancellationToken);

    public Task<UiCapabilityResult<InvestigationSnapshot>> ReplyAsync(string investigationId, long expectedVersion, string clarificationId,
        string factualAnswer, CancellationToken cancellationToken = default) => RunAsync(async context =>
        {
            Text(factualAnswer, 4000);
            var row = await OwnedAsync(context, investigationId, cancellationToken);
            var snapshot = await SnapshotAsync(context, row, Unprotect(row), cancellationToken);
            if (!snapshot.Clarifications.Any(question => question.Id == clarificationId)) throw new ArgumentException("The clarification is no longer current.");
            await SubmitAsync(context, row, expectedVersion, factualAnswer, $"reply-{clarificationId}", cancellationToken);
            return await SnapshotAsync(context, row, Unprotect(row), cancellationToken);
        }, cancellationToken);

    public Task<UiCapabilityResult<InvestigationSnapshot>> PauseAsync(string investigationId, long expectedVersion, CancellationToken cancellationToken = default) =>
        ChangeStateAsync(investigationId, expectedVersion, true, cancellationToken);
    public Task<UiCapabilityResult<InvestigationSnapshot>> ResumeAsync(string investigationId, long expectedVersion, CancellationToken cancellationToken = default) =>
        ChangeStateAsync(investigationId, expectedVersion, false, cancellationToken);

    private Task<UiCapabilityResult<InvestigationSnapshot>> ChangeStateAsync(string id, long version, bool pause, CancellationToken ct) => RunAsync(async context =>
    {
        var row = await OwnedAsync(context, id, ct);
        var store = context.Services.GetRequiredService<IEllaiSessionStore>();
        var current = await store.GetAsync(id, ct) ?? throw new UnauthorizedAccessException();
        if (current.SessionVersion != version || current.Status is "Cancelled" or "Completed") throw new InvalidOperationException("The investigation changed.");
        var next = await store.TryUpdateAsync(current with { Status = pause ? "Paused" : "Active", SessionVersion = version + 1,
            ControlFence = current.ControlFence + 1, InteractionEpoch = current.InteractionEpoch + 1, SpeechEpoch = current.SpeechEpoch + 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow }, version, ct) ?? throw new InvalidOperationException("The investigation changed.");
        if (!pause) await SubmitAsync(context, row, next.SessionVersion, "Resume the factual investigation using current authorized evidence. " + Unprotect(row).Objective, "resume", ct);
        return await SnapshotAsync(context, row, Unprotect(row), ct);
    }, ct);

    public Task<UiCapabilityResult<InvestigationSnapshot>> ProposeAdministrativeActionAsync(string investigationId, long expectedVersion,
        string summary, CancellationToken cancellationToken = default) => RunAsync(async context =>
        {
            Text(summary, 4000);
            var row = await OwnedAsync(context, investigationId, cancellationToken);
            await SubmitAsync(context, row, expectedVersion, "Prepare an administrative action for explicit human review: " + summary, "propose", cancellationToken);
            return await SnapshotAsync(context, row, Unprotect(row), cancellationToken);
        }, cancellationToken);

    public Task<UiCapabilityResult<InvestigationSnapshot>> ReviewProposalAsync(InvestigationProposalReview review, CancellationToken cancellationToken = default) =>
        RunAsync(async context =>
        {
            Text(review.Reason, 500);
            var row = await OwnedAsync(context, review.InvestigationId, cancellationToken);
            var session = await context.Services.GetRequiredService<IEllaiSessionStore>().GetAsync(row.SessionId, cancellationToken);
            if (session?.SessionVersion != review.ExpectedVersion || session.Status != "Active") throw new InvalidOperationException("The investigation changed.");
            var pending = await context.Review.GetActiveReviewAsync(row.SessionId, cancellationToken);
            if (pending?.RequestId != review.ProposalId) throw new InvalidOperationException("The proposal is no longer awaiting review.");
            var stored = Unprotect(row);
            var updated = Protect(row.SessionId, stored with { Reviews = (stored.Reviews ?? [])
                .Append(new ReviewIntent(review.ProposalId, review.Approve, review.Reason.Trim(), context.Actor.ActorId, DateTimeOffset.UtcNow))
                .TakeLast(100).ToArray() });
            // Record the human's intent before execution; only execution receipts prove an effect.
            if (await context.Db.Set<AgentInvestigationEntity>().Where(item => item.SessionId == row.SessionId && item.ProtectedRequest == row.ProtectedRequest)
                    .ExecuteUpdateAsync(update => update.SetProperty(item => item.ProtectedRequest, updated), cancellationToken) != 1)
                throw new InvalidOperationException("The investigation changed.");
            row.ProtectedRequest = updated;
            if (review.Approve)
            {
                var receipts = await context.Review.ApproveSelectedAsync(row.SessionId, pending.RequestId, pending.Actions.Select(action => action.ActionId).ToArray(), cancellationToken);
                if (receipts is null) throw new InvalidOperationException("Approval was not confirmed.");
            }
            else if (!await context.Review.RejectReviewAsync(row.SessionId, pending.RequestId, cancellationToken)) throw new InvalidOperationException("Review was not confirmed.");
            return await SnapshotAsync(context, row, Unprotect(row), cancellationToken);
        }, cancellationToken);

    public Task<UiCapabilityResult<InvestigationExecutionAndVerification>> GetExecutionAndVerificationAsync(string investigationId,
        CancellationToken cancellationToken = default) => RunAsync(async context =>
        {
            var row = await OwnedAsync(context, investigationId, cancellationToken);
            var snapshot = await SnapshotAsync(context, row, Unprotect(row), cancellationToken);
            var receipts = await context.Review.ListReceiptsAsync(row.SessionId, cancellationToken);
            if (receipts is null) throw new InvalidOperationException("Execution receipts could not be read.");
            return new InvestigationExecutionAndVerification(row.SessionId, snapshot.Version, snapshot.ExecutionStatus, snapshot.VerificationStatus,
                snapshot.ResultSummary, receipts.Select(receipt => new InvestigationExecutionCheck(receipt.ActionId,
                    receipt.Status == nameof(EllaiReceiptStatus.Applied), receipt.Status)).ToArray(), snapshot.UpdatedAtUtc!.Value, false,
                "Only applied server receipts confirm an effect. Reasoning completion alone does not confirm execution.");
        }, cancellationToken);

    private async Task<InvestigationSnapshot> SnapshotAsync(Context context, AgentInvestigationEntity row, StoredRequest request, CancellationToken ct)
    {
        var session = await context.Services.GetRequiredService<IEllaiSessionStore>().GetAsync(row.SessionId, ct) ?? throw new UnauthorizedAccessException();
        var last = await context.Db.EllaiUtterances.AsNoTracking().Where(turn => turn.SessionId == row.SessionId).OrderByDescending(turn => turn.Sequence).FirstOrDefaultAsync(ct);
        var decision = last?.DecisionJson is { } json ? JsonSerializer.Deserialize<EllaiDecision>(json) : null;
        var review = await context.Review.GetActiveReviewAsync(row.SessionId, ct);
        var receipts = await context.Review.ListReceiptsAsync(row.SessionId, ct);
        var applied = receipts is { Count: > 0 } && receipts.All(receipt => receipt.Status == nameof(EllaiReceiptStatus.Applied));
        var execution = receipts is not { Count: > 0 } ? InvestigationExecutionStatus.NotStarted
            : applied ? InvestigationExecutionStatus.Completed : receipts.Any(receipt => receipt.Status == nameof(EllaiReceiptStatus.Failed))
                ? InvestigationExecutionStatus.Failed : InvestigationExecutionStatus.Partial;
        var status = session.Status == "Cancelled" || last?.Outcome == "Cancelled" ? InvestigationStatus.Failed : session.Status == "Paused" ? InvestigationStatus.Paused : review is not null ? InvestigationStatus.AwaitingApproval
            : last is null || last.Outcome is "Queued" or "Processing" ? InvestigationStatus.GatheringEvidence
            : last.Outcome == "TerminalFailure" ? InvestigationStatus.Failed
            : decision?.Kind == DecisionKind.NeedInput ? InvestigationStatus.NeedsInput
            : decision is null ? InvestigationStatus.Partial : decision.PlanProposal is not null && !applied ? InvestigationStatus.Partial : InvestigationStatus.Completed;
        return new(row.SessionId, status, request.Title, request.Objective,
            decision?.Claims.SelectMany(claim => claim.Evidence).Distinct().Select(source => new InvestigationEvidenceReference(source.EvidenceId, source.SourceKind, source.Version)).ToArray() ?? [],
            decision?.Questions.Select(question => new InvestigationClarification(question.QuestionId, question.Text)).ToArray() ?? [],
            review is null ? null : new(review.RequestId, string.Join("\n", review.Actions.Select(action => $"{action.Label}: {action.Detail}")), true, false),
            false, Version: session.SessionVersion, ExecutionStatus: execution,
            VerificationStatus: applied ? InvestigationVerificationStatus.Passed : receipts is { Count: > 0 } ? InvestigationVerificationStatus.Incomplete : InvestigationVerificationStatus.NotStarted,
            ResultSummary: decision?.DisplayText ?? last?.AssistantText, UpdatedAtUtc: session.UpdatedAtUtc,
            Notice: "Factual and administrative assistance only. Case grants and human approval remain authoritative.");
    }

    private static async Task SubmitAsync(Context context, AgentInvestigationEntity row, long version, string text, string operation, CancellationToken ct)
    {
        var result = await context.Review.SubmitUtteranceAsync(new($"investigation-{operation}-{version}", row.SessionId, version,
            text, null, "typed", version, "investigations"), ct);
        if (result.Status != EllaiClientOperationStatus.Accepted) throw new InvalidOperationException("The investigation turn was not accepted.");
    }
    private async Task<AgentInvestigationEntity> OwnedAsync(Context context, string id, CancellationToken ct)
    {
        Text(id, 160);
        var row = await context.Db.Set<AgentInvestigationEntity>().AsNoTracking().SingleOrDefaultAsync(item => item.SessionId == id && item.OwnerActorId == context.Actor.ActorId, ct)
            ?? throw new UnauthorizedAccessException();
        if (Unprotect(row).ScopeHash != ScopeHash(context.Scope)) throw new UnauthorizedAccessException();
        return row;
    }
    private async Task<UiCapabilityResult<T>> RunAsync<T>(Func<Context, Task<T>> action, CancellationToken ct)
    {
        try
        {
            var actor = ActorContext.FromValidatedPrincipal((await authentication.GetAuthenticationStateAsync()).User);
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var current = await services.GetRequiredService<IScopeService>().GetAsync(actor, ct);
            if (current.AccessEpoch != actor.AccessEpoch || current.ExpiresAtUtc <= DateTimeOffset.UtcNow || current.Grants.Count == 0)
                return Result<T>(UiCapabilityResultCode.Denied);
            return Result(UiCapabilityResultCode.Success, await action(new(services, services.GetRequiredService<SwiftAidDbContext>(), actor,
                current, new EllaiReviewGatewayClient(authentication, services))));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is UnauthorizedAccessException or ScopeUnavailableException) { return Result<T>(UiCapabilityResultCode.Denied); }
        catch (ArgumentException) { return Result<T>(UiCapabilityResultCode.InvalidRequest); }
        catch (DbUpdateConcurrencyException) { return Result<T>(UiCapabilityResultCode.Conflict); }
        catch (InvalidOperationException) { return Result<T>(UiCapabilityResultCode.Conflict); }
        catch (Exception error) { logger.LogWarning(error, "Investigation request could not be completed."); return Result<T>(UiCapabilityResultCode.Unavailable); }
    }
    private StoredRequest Unprotect(AgentInvestigationEntity row) => JsonSerializer.Deserialize<StoredRequest>(
        protection.CreateProtector("SwiftAid.Investigation.v1", row.SessionId).Unprotect(row.ProtectedRequest)) ?? throw new InvalidDataException("Invalid investigation state.");
    private string Protect(string id, StoredRequest request) => protection.CreateProtector("SwiftAid.Investigation.v1", id).Protect(JsonSerializer.Serialize(request));
    private static string ScopeHash(ScopeManifest scope) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(scope.Grants.OrderBy(grant => grant.GroupId)
        .Select(grant => new { grant.GroupId, grant.OrganizationIds, grant.WorkspaceIds, grant.CaseIds, grant.CountryCodes, grant.RegionIds, grant.SensitivityCodes,
            grant.SectorCodes, grant.Operations, grant.CanAccessJurisdictionalCases }))));
    private static void Text(string value, int limit) { if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value.Any(c => char.IsControl(c) && c is not '\r' and not '\n')) throw new ArgumentException("Invalid investigation input."); }
    private static UiCapabilityResult<T> Result<T>(UiCapabilityResultCode code, T? value = default) => new(code, value,
        new("investigations", UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, code == UiCapabilityResultCode.Success));
}
