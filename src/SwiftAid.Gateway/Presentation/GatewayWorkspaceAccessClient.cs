using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Client.Presentation;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Gateway.Presentation;

/// <summary>Membership-scoped display context. Selection never creates case access grants.</summary>
public sealed class GatewayWorkspaceAccessClient(
    AuthenticationStateProvider authentication,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<GatewayWorkspaceAccessClient> logger) : IWorkspaceAccessClient
{
    private const string Capability = "workspace-display-context";
    private string? _actorId;
    private string? _selected;

    public Task<UiCapabilityResult<WorkspaceAccessState>> GetStateAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(null, cancellationToken);

    public Task<UiCapabilityResult<WorkspaceAccessState>> SelectWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default) =>
        LoadAsync(workspaceId, cancellationToken);

    private async Task<UiCapabilityResult<WorkspaceAccessState>> LoadAsync(string? requested, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var actor = ActorContext.FromValidatedPrincipal((await authentication.GetAuthenticationStateAsync()).User);
            if (_actorId != actor.ActorId) { _selected = null; _actorId = actor.ActorId; }
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SwiftAidDbContext>();
            var now = clock.GetUtcNow();
            var current = await (from person in db.Actors
                                 join state in db.ActorAccessStates on person.ActorId equals state.ActorId
                                 where person.ActorId == actor.ActorId && state.AccessEpoch == actor.AccessEpoch &&
                                     person.Status == PersistentActorStatus.Active &&
                                     (person.ExpiresAtUtc == null || person.ExpiresAtUtc > now)
                                 select person.ActorId).AnyAsync(ct);
            if (!current) return Denied();
            var memberships = await scope.ServiceProvider.GetRequiredService<IOrganizationMembershipService>().GetMineAsync(actor.ActorId, ct);
            var workspaces = memberships.Where(m => m.OrganizationStatus == "Active" && m.MembershipStatus == "Active" &&
                    m.ValidFromUtc <= now && (m.ExpiresAtUtc == null || m.ExpiresAtUtc > now))
                .SelectMany(m => m.WorkspaceIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(id => new WorkspaceAccessOption(id, id)).ToArray();
            if (requested is not null && !workspaces.Any(w => w.Id == requested)) return Denied();
            _selected = requested ?? (workspaces.Any(w => w.Id == _selected) ? _selected : workspaces.FirstOrDefault()?.Id);
            return new(UiCapabilityResultCode.Success, new(workspaces, _selected),
                new(Capability, UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService, now, true));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return Denied(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("Workspace display unavailable. Failure type: {FailureType}", error.GetType().Name);
            return UiCapabilityResults.Unavailable<WorkspaceAccessState>(Capability);
        }
    }

    private UiCapabilityResult<WorkspaceAccessState> Denied()
    {
        _selected = null;
        return new(UiCapabilityResultCode.Denied, null,
            new(Capability, UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService, clock.GetUtcNow(), false),
            "Workspace access has changed. Sign in again or ask your organization to review your access.");
    }
}
