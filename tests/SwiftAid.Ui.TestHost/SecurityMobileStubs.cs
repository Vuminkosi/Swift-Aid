using SwiftAid.Client.Presentation;

namespace SwiftAid.Mobile.Services.Http
{
    public sealed class CookieSessionHttpClient
    {
        public static CookieSessionHttpClient Shared(string? address) => new();
    }
}

namespace SwiftAid.Mobile.Services.Account
{
    public enum PasswordAuthStatus { Succeeded, SignedOut, Unavailable }
    public sealed record PasswordAuthOutcome(PasswordAuthStatus Status, string? Message = null);
    public sealed class HttpPasswordAuthClient
    {
        public HttpPasswordAuthClient(SwiftAid.Mobile.Services.Http.CookieSessionHttpClient session) => ArgumentNullException.ThrowIfNull(session);
        public Task<PasswordAuthOutcome> ChangePasswordAsync(string currentPassword, string newPassword) =>
            Task.FromResult(new PasswordAuthOutcome(PasswordAuthStatus.Unavailable));
    }
    public sealed class HttpUserProfileClient : IUserProfileClient
    {
        public HttpUserProfileClient(SwiftAid.Mobile.Services.Http.CookieSessionHttpClient session) => ArgumentNullException.ThrowIfNull(session);
        private readonly UnavailableUserProfileClient _inner = new();
        public Task<UiCapabilityResult<UserProfileState>> GetAsync(CancellationToken cancellationToken = default) => _inner.GetAsync(cancellationToken);
        public Task<UiCapabilityResult<UserProfileState>> SaveAsync(UserProfileEdit edit, CancellationToken cancellationToken = default) => _inner.SaveAsync(edit, cancellationToken);
        public Task<UiCapabilityResult<UserProfileState>> UploadAvatarAsync(UserProfileAvatarUpload upload, CancellationToken cancellationToken = default) => _inner.UploadAvatarAsync(upload, cancellationToken);
        public Task<UiCapabilityResult<UserProfileState>> DeleteAvatarAsync(CancellationToken cancellationToken = default) => _inner.DeleteAvatarAsync(cancellationToken);
        public Task<UiCapabilityResult<UserProfileState>> RequestEmailChangeAsync(string newEmail, string currentPassword, CancellationToken cancellationToken = default) => _inner.RequestEmailChangeAsync(newEmail, currentPassword, cancellationToken);
        public Task<UiCapabilityResult<UserProfileState>> ConfirmEmailChangeAsync(string code, CancellationToken cancellationToken = default) => _inner.ConfirmEmailChangeAsync(code, cancellationToken);
    }
}
