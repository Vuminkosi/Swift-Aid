namespace SwiftAid.Client.Presentation;

public sealed record UserProfileState(
    bool IsSignedIn,
    string? DisplayName,
    string? Phone,
    string? Country,
    string? Region,
    string? Locality,
    string? Email,
    bool EmailVerified,
    string? StatusMessage = null,
    bool StatusIsError = false);

public sealed record UserProfileEdit(
    string? DisplayName,
    string? Phone,
    string? Country,
    string? Region,
    string? Locality);

/// <summary>Self-asserted profile fields; email verification and membership stay with their owners.</summary>
public interface IUserProfileClient
{
    Task<UiCapabilityResult<UserProfileState>> GetAsync(CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<UserProfileState>> SaveAsync(
        UserProfileEdit edit,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableUserProfileClient : IUserProfileClient
{
    public Task<UiCapabilityResult<UserProfileState>> GetAsync(CancellationToken cancellationToken = default) =>
        Unavailable(cancellationToken);

    public Task<UiCapabilityResult<UserProfileState>> SaveAsync(
        UserProfileEdit edit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return Unavailable(cancellationToken);
    }

    private static Task<UiCapabilityResult<UserProfileState>> Unavailable(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<UserProfileState>("user-profile"));
    }
}
