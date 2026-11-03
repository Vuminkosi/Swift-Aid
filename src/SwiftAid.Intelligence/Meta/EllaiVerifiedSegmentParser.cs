using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Extracts complete, independently validated decisions from a streaming envelope.</summary>
internal sealed class EllaiVerifiedSegmentParser
{
    internal sealed record Segment(string Text, IReadOnlyList<GroundedClaim> Claims);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly StringBuilder _text = new();
    private int _processed;
    private int _totalCharacters;
    private bool _rejected;

    public IReadOnlyList<Segment> Append(string delta, IReadOnlySet<EvidenceRef> observed)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(observed);
        if (_rejected) return [];
        _text.Append(delta);
        var bytes = Encoding.UTF8.GetBytes(_text.ToString());
        var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
        var results = new List<Segment>();
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject || !reader.Read() ||
                reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("stream_segments") ||
                !reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                return results;
            var position = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject) { _rejected = true; break; }
                if (!JsonDocument.TryParseValue(ref reader, out var document)) break;
                using (document)
                {
                    if (position++ < _processed) continue;
                    _processed++;
                    var item = document.RootElement.Deserialize<Segment>(Json);
                    if (item is null || item.Text is null || item.Claims is null) { _rejected = true; break; }
                    // A segment based on observed evidence must identify its
                    // supporting claims; an empty array cannot establish that
                    // the text is grounded in those observations.
                    if (observed.Count > 0 && (item.Claims.Count == 0 || item.Claims.Any(claim =>
                            claim is null || claim.Evidence is null || claim.Evidence.Count == 0 ||
                            string.IsNullOrWhiteSpace(claim.Text) ||
                            !string.Equals(claim.Text.Trim(), item.Text.Trim(), StringComparison.Ordinal))))
                    {
                        _rejected = true;
                        break;
                    }
                    var decision = new EllaiDecision(EllaiDecision.CurrentSchemaVersion, DecisionKind.Answer,
                        item.Text, item.Text, item.Claims, [], [], null, [], null, null);
                    if (
                        EllaiDecisionValidator.Validate(decision).Count != 0 ||
                        decision.Claims.Any(claim => claim?.Evidence is { } refs &&
                            refs.Any(reference => reference is null || !observed.Contains(reference))))
                    {
                        _rejected = true;
                        break;
                    }
                    var segment = decision.DisplayText.Trim();
                    var newLength = _totalCharacters + (position == 1 ? 0 : 1) + segment.Length;
                    if (newLength > EllaiDecisionValidator.MaxDisplayChars) { _rejected = true; break; }
                    _totalCharacters = newLength;
                    results.Add(item with { Text = segment });
                }
            }
        }
        catch (JsonException) { _rejected = true; }
        return results;
    }
}
