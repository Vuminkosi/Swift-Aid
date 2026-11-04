namespace SwiftAid.Infrastructure.Configuration;

public sealed class QdrantOptions
{
    public const string SectionName = "Qdrant";

    public bool Enabled { get; set; }

    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;
}
