namespace SwiftAid.Infrastructure.Configuration;

/// <summary>Public web origin used to build absolute account links in delivered mail.</summary>
public sealed class PublicSiteOptions
{
    public string BaseUrl { get; set; } = string.Empty;
}
