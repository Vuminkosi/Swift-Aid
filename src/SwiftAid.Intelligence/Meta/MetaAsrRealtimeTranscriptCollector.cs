using System.Text.Json;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Extracts only provider-final utterances from Muse Voice Transcribe
/// events. PUSH_TO_TALK completes with transcript/final; ENDPOINTING and
/// DIARIZATION complete with speechComplete/turnId.</summary>
public sealed class MetaAsrRealtimeTranscriptCollector
{
    private readonly List<MetaAsrRealtimeTranscript> _transcripts = [];
    private readonly HashSet<long> _completedTurnIds = [];

    public IReadOnlyList<MetaAsrRealtimeTranscript> Turns => _transcripts
        .OrderBy(turn => turn.TurnId ?? long.MaxValue)
        .ToArray();

    public IReadOnlyList<string> Transcripts => Turns.Select(turn => turn.Text).ToArray();

    public bool TryReadPartial(string json, out MetaAsrRealtimePartialTranscript? partial)
    {
        partial = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeValue)
                || typeValue.ValueKind != JsonValueKind.String
                || !string.Equals(typeValue.GetString(), "transcript", StringComparison.Ordinal)
                || !root.TryGetProperty("final", out var finalValue)
                || finalValue.ValueKind != JsonValueKind.False
                || !root.TryGetProperty("transcript", out var transcriptValue)
                || transcriptValue.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var transcript = transcriptValue.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(transcript) || transcript.Length > 8_000)
            {
                return false;
            }

            long? turnId = null;
            if (root.TryGetProperty("turnId", out var turnValue))
            {
                if (turnValue.ValueKind != JsonValueKind.Number
                    || !turnValue.TryGetInt64(out var parsedTurnId)
                    || parsedTurnId < 1)
                {
                    return false;
                }

                turnId = parsedTurnId;
            }

            partial = new MetaAsrRealtimePartialTranscript(turnId, transcript);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public bool TryAppend(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeValue)
                || typeValue.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("transcript", out var transcriptValue)
                || transcriptValue.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var type = typeValue.GetString();
            var transcript = transcriptValue.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(transcript))
            {
                return false;
            }

            if (string.Equals(type, "speechComplete", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("turnId", out var turnValue)
                    || turnValue.ValueKind != JsonValueKind.Number
                    || !turnValue.TryGetInt64(out var turnId)
                    || turnId < 1
                    || !_completedTurnIds.Add(turnId))
                {
                    return false;
                }

                _transcripts.Add(new MetaAsrRealtimeTranscript(turnId, transcript));
                return true;
            }

            if (!string.Equals(type, "transcript", StringComparison.Ordinal)
                || !root.TryGetProperty("final", out var finalValue)
                || finalValue.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            long? finalTurnId = null;
            if (root.TryGetProperty("turnId", out var finalTurnValue))
            {
                if (finalTurnValue.ValueKind != JsonValueKind.Number
                    || !finalTurnValue.TryGetInt64(out var parsedTurnId)
                    || parsedTurnId < 1
                    || !_completedTurnIds.Add(parsedTurnId))
                {
                    return false;
                }

                finalTurnId = parsedTurnId;
            }

            _transcripts.Add(new MetaAsrRealtimeTranscript(finalTurnId, transcript));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed record MetaAsrRealtimeTranscript(long? TurnId, string Text);

public sealed record MetaAsrRealtimePartialTranscript(long? TurnId, string Text);
