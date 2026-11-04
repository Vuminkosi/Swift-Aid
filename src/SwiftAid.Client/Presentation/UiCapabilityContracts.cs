namespace SwiftAid.Client.Presentation;

public enum UiCapabilityResultCode
{
    Success,
    Unavailable,
    Denied,
    Conflict,
    Cancelled,
    Retryable,
    InvalidRequest
}

public enum UiCapabilityProvenance
{
    LiveService,
    SyntheticDevelopment,
    LocalOnly
}

public sealed record UiCapabilityMetadata(
    string Capability,
    string ContractVersion,
    UiCapabilityProvenance Provenance,
    DateTimeOffset UpdatedAtUtc,
    bool IsAvailable,
    string? Notice = null)
{
    public bool IsSynthetic => Provenance == UiCapabilityProvenance.SyntheticDevelopment;
}

public sealed record UiCapabilityResult<T>(
    UiCapabilityResultCode Code,
    T? Value,
    UiCapabilityMetadata Metadata,
    string? Message = null)
{
    public bool Succeeded => Code == UiCapabilityResultCode.Success;
}

public sealed record PageRequest(int PageNumber = 1, int PageSize = 25)
{
    public bool IsValid => PageNumber > 0 && PageSize is > 0 and <= 100;
}

public sealed record Page<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount,
    bool HasMore);

public static class UiCapabilityResults
{
    public const string ContractVersion = "1";

    public static UiCapabilityResult<T> Unavailable<T>(string capability) => new(
        UiCapabilityResultCode.Unavailable,
        default,
        new UiCapabilityMetadata(
            capability,
            ContractVersion,
            UiCapabilityProvenance.LiveService,
            DateTimeOffset.UtcNow,
            IsAvailable: false,
            "This capability is not connected in this environment."),
        "This capability is not connected in this environment.");

    public static UiCapabilityMetadata SyntheticMetadata(string capability, string notice) => new(
        capability,
        ContractVersion,
        UiCapabilityProvenance.SyntheticDevelopment,
        DateTimeOffset.UtcNow,
        IsAvailable: true,
        notice);

    public static UiCapabilityMetadata UnavailableMetadata(string capability, string notice) => new(
        capability,
        ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        IsAvailable: false,
        notice);
}
