using System.Text;
using System.Text.Json;

namespace SwiftAid.Application.Ellai;

/// <summary>Extracts only complete sentence-sized pieces of the final
/// decision's displayText JSON string while the model response is arriving.
/// Tool-call JSON and spoken-text metadata are never surfaced as answer text.</summary>
public sealed class EllaiDecisionTextStreamParser
{
    private const string FinalDecisionKey = "\"final_decision\"";
    private const string DisplayTextKey = "\"displayText\"";
    private readonly StringBuilder _response = new();
    private int _valueStart = -1;
    private int _scanPosition;
    private int _segmentStart;
    private bool _closed;

    public IReadOnlyList<string> Append(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (_closed || delta.Length == 0)
        {
            return Array.Empty<string>();
        }

        _response.Append(delta);
        LocateValue();
        if (_valueStart < 0)
        {
            return Array.Empty<string>();
        }

        var sentences = new List<string>();
        while (_scanPosition < _response.Length)
        {
            var current = _response[_scanPosition];
            if (current == '\\')
            {
                var escapeLength = EscapeLength(_scanPosition);
                if (_scanPosition + escapeLength > _response.Length)
                {
                    break;
                }
                _scanPosition += escapeLength;
                continue;
            }
            if (current == '"')
            {
                _closed = true;
                break;
            }

            if (current is '.' or '?' or '!')
            {
                var next = _scanPosition + 1;
                if (next >= _response.Length)
                {
                    break;
                }
                if (next < _response.Length && _response[next] == '"')
                {
                    Emit(next, sentences);
                    _scanPosition = next;
                    _segmentStart = next;
                    continue;
                }
                if (next < _response.Length && char.IsWhiteSpace(_response[next]))
                {
                    while (next < _response.Length && char.IsWhiteSpace(_response[next]))
                    {
                        next++;
                    }
                    Emit(next, sentences);
                    _scanPosition = next;
                    _segmentStart = next;
                    continue;
                }
            }
            _scanPosition++;
        }

        return sentences;
    }

    public string FlushVerified(string validatedDisplayText)
    {
        ArgumentNullException.ThrowIfNull(validatedDisplayText);
        var emitted = _response.Length == 0
            ? string.Empty
            : _emittedText.ToString();
        if (!validatedDisplayText.StartsWith(emitted, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return validatedDisplayText[emitted.Length..].Trim();
    }

    private readonly StringBuilder _emittedText = new();

    private void LocateValue()
    {
        if (_valueStart >= 0)
        {
            return;
        }

        var response = _response.ToString();
        var decision = response.IndexOf(FinalDecisionKey, StringComparison.Ordinal);
        if (decision < 0)
        {
            return;
        }

        var key = response.IndexOf(DisplayTextKey, decision + FinalDecisionKey.Length, StringComparison.Ordinal);
        if (key < 0)
        {
            return;
        }

        var colon = response.IndexOf(':', key + DisplayTextKey.Length);
        if (colon < 0)
        {
            return;
        }

        var quote = colon + 1;
        while (quote < response.Length && char.IsWhiteSpace(response[quote]))
        {
            quote++;
        }
        if (quote >= response.Length || response[quote] != '"')
        {
            return;
        }

        _valueStart = quote + 1;
        _segmentStart = _valueStart;
        _scanPosition = _valueStart;
    }

    private int EscapeLength(int slashPosition)
    {
        if (slashPosition + 1 >= _response.Length)
        {
            return 2;
        }

        return _response[slashPosition + 1] == 'u' ? 6 : 2;
    }

    private void Emit(int endExclusive, ICollection<string> sentences)
    {
        if (endExclusive <= _segmentStart)
        {
            return;
        }

        var encoded = "\"" + _response.ToString(_segmentStart, endExclusive - _segmentStart) + "\"";
        string? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<string>(encoded);
        }
        catch (JsonException)
        {
            return;
        }

        var sentence = decoded?.Trim();
        if (!string.IsNullOrEmpty(sentence))
        {
            sentences.Add(sentence);
            _emittedText.Append(decoded);
        }
    }
}
