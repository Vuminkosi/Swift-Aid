using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Mobile.Services.Authentication;

namespace SwiftAid.Ui.TestHost;

public sealed class SessionHistoryFixture : IMobileAuthenticationSession, IStaffSessionHistoryClient
{
    public event EventHandler? AuthenticationStateChanged;
    public bool SignedIn { get; private set; } = true;
    public bool Allowed { get; private set; } = true;
    public void ChangeIdentity(bool signedIn, bool allowed)
    {
        SignedIn = signedIn;
        Allowed = allowed;
        AuthenticationStateChanged?.Invoke(this, EventArgs.Empty);
    }
    public Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default) => Task.FromResult(SignedIn);
    public Task<bool> HasPendingSignOutAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> SignInAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<MobileAuthenticationSignOutResult> SignOutAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new MobileAuthenticationSignOutResult(true, true));
    public Task<UiCapabilityResult<SessionHistoryPage>> ReadAsync(Guid sessionId, long? beforeVersion, CancellationToken cancellationToken = default)
    {
        var code = SignedIn && Allowed ? UiCapabilityResultCode.Success : UiCapabilityResultCode.Denied;
        var page = code == UiCapabilityResultCode.Success ? new SessionHistoryPage(sessionId,
            [new(1, "synthetic-actor", "synthetic review", DateTimeOffset.UtcNow, "Created", "Draft", null, null)], null) : null;
        return Task.FromResult(new UiCapabilityResult<SessionHistoryPage>(code, page,
            new("session-history", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, code == UiCapabilityResultCode.Success)));
    }
}
