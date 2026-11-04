namespace SwiftAid.Contracts.Commands;

/// <summary>Server-created binding to immutable private original evidence; never a public object URL.</summary>
public sealed record RecordedAttachment(Guid ObjectId, string FileName, string ContentType, long ByteLength, string Sha256)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => ObjectId != Guid.Empty && ByteLength is > 0 and <= 10 * 1024 * 1024
        && !string.IsNullOrWhiteSpace(FileName) && FileName.Length <= 100
        && !FileName.Any(character => char.IsControl(character) || "/\\:*?\"<>|".Contains(character))
        && ContentType is "image/jpeg" or "image/png" or "application/pdf" or "text/plain; charset=utf-8"
        && Sha256 is { Length: 64 } && Sha256.All(Uri.IsHexDigit);
}
