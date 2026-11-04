using SwiftAid.Client.Presentation;

namespace SwiftAid.Ui.TestHost;

public sealed class SecurityLoadingFixture : IAccountPresentationClient, IUserProfileClient
{
    private readonly TaskCompletionSource<UiCapabilityResult<UserProfileState>> _profile = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _reads;
    public Task<UiCapabilityResult<AccountPresentationState>> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (++_reads == 1) return Task.FromResult(UiCapabilityResults.Unavailable<AccountPresentationState>("fixture"));
        return Task.FromResult(new UiCapabilityResult<AccountPresentationState>(UiCapabilityResultCode.Success,
            new(true, "Fixture", [new("example", "Example provider", true, IsConnected: true)]),
            UiCapabilityResults.SyntheticMetadata("account", "Fixture")));
    }
    public Task<UiCapabilityResult<AccountPresentationState>> BeginSignInAsync(string providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UiCapabilityResult<AccountPresentationState>> SignOutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UiCapabilityResult<UserProfileState>> GetAsync(CancellationToken cancellationToken = default) => _profile.Task.WaitAsync(cancellationToken);
    public void ReleaseProfile() => _profile.TrySetResult(UiCapabilityResults.Unavailable<UserProfileState>("fixture"));
    public Task<UiCapabilityResult<UserProfileState>> SaveAsync(UserProfileEdit edit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UiCapabilityResult<UserProfileState>> UploadAvatarAsync(UserProfileAvatarUpload upload, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UiCapabilityResult<UserProfileState>> DeleteAvatarAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UiCapabilityResult<UserProfileState>> RequestEmailChangeAsync(string newEmail, string currentPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UiCapabilityResult<UserProfileState>> ConfirmEmailChangeAsync(string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
