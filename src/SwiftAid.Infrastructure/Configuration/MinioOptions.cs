namespace SwiftAid.Infrastructure.Configuration;

public sealed class MinioOptions
{
    public const string SectionName = "Minio";

    public bool Enabled { get; set; }

    public string Endpoint { get; set; } = string.Empty;

    public string Bucket { get; set; } = string.Empty;

    public long MaxObjectSizeBytes { get; set; }

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public bool UseSsl { get; set; }
}
