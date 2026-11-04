using System.Security.Claims;
using SwiftAid.Client.Presentation;
using SwiftAid.Gateway.Endpoints;

namespace SwiftAid.Integration.Tests;

internal sealed class SyntheticWorkspaceAccessRequest : IWorkspaceAccessRequest
{
    public bool Current { get; set; } = true;
    public bool Unavailable { get; set; }
    public Task<UiCapabilityResult<WorkspaceAccessState>> GetAsync(ClaimsPrincipal principal, string? workspace, CancellationToken ct) =>
        Task.FromResult(new UiCapabilityResult<WorkspaceAccessState>(Unavailable ? UiCapabilityResultCode.Unavailable : Current ? UiCapabilityResultCode.Success : UiCapabilityResultCode.Denied,
            Current && !Unavailable ? new WorkspaceAccessState([], null) : null,
            new("workspace", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, Current && !Unavailable)));
}
