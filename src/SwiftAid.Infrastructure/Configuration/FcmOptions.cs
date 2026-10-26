namespace SwiftAid.Infrastructure.Configuration;

public sealed class FcmOptions
{
    public const string SectionName = "Fcm";

    public bool Enabled { get; set; }

    public string ProjectId { get; set; } = string.Empty;

    public string CredentialsFilePath { get; set; } = string.Empty;
}
