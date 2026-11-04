using System.Text.Json;

namespace SwiftAid.Contracts.Intake;

/// <summary>Private editable form state inside the protected local envelope. It is never sent to intake.</summary>
public sealed record GuestDraftFormSnapshot(int SchemaVersion, string Json)
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumJsonCharacters = 131072;

    public bool IsValid()
    {
        if (SchemaVersion is not (1 or CurrentSchemaVersion) || string.IsNullOrWhiteSpace(Json) || Json.Length > MaximumJsonCharacters)
            return false;
        try
        {
            using var document = JsonDocument.Parse(Json, new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    public override string ToString() => $"GuestDraftFormSnapshot {{ SchemaVersion = {SchemaVersion}, Json = [redacted] }}";
}
