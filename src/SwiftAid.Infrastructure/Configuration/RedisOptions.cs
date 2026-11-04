namespace SwiftAid.Infrastructure.Configuration;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    public const long MaximumSupportedTtlSeconds = 86400;

    public bool Enabled { get; set; }

    public string ConnectionString { get; set; } = string.Empty;

    public string InstanceName { get; set; } = "swiftaid:";

    public long MaximumTtlSeconds { get; set; } = 3600;
}
