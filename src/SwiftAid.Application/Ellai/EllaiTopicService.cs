using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Topic ledger entry with stable identity and checkpoints.</summary>
public sealed record EllaiTopic(
    string TopicId,
    string Title,
    IReadOnlyList<string> Labels,
    string? EntityRef,
    string? EntityVersion,
    string? CheckpointId,
    string CoverageState);

/// <summary>Scoped topic recall with bounded detours (ELL-T09 groundwork).
/// Ordered mentions resolve first ("the second issue"); semantic candidates
/// stay explicit with an ambiguity flag. Return frames stack to five deep;
/// a sixth requires an explicit replace-or-return choice. Revisit re-reads
/// current evidence — old explanations are never presented as current.</summary>
public sealed class EllaiTopicService
{
    public const int MaxReturnFrames = 5;

    private readonly object _gate = new();
    private readonly List<EllaiTopic> _topics = new();
    private readonly Stack<GuidanceReturnFrame> _returnStack = new();

    public EllaiTopic Track(string title, IReadOnlyList<string> labels, string? entityRef, string? entityVersion)
    {
        var topic = new EllaiTopic(
            $"topic-{Guid.NewGuid():N}", title, labels, entityRef, entityVersion, null, "Open");
        lock (_gate)
        {
            _topics.Add(topic);
        }

        return topic;
    }

    public IReadOnlyList<EllaiTopic> Resolve(string reference, out bool ambiguous)
    {
        lock (_gate)
        {
            if (TryOrdinal(reference, out var ordinal) && ordinal >= 1 && ordinal <= _topics.Count)
            {
                ambiguous = false;
                return new[] { _topics[ordinal - 1] };
            }

            var matches = _topics
                .Where(t => t.Title.Contains(reference, StringComparison.OrdinalIgnoreCase)
                    || t.Labels.Any(l => l.Contains(reference, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            ambiguous = matches.Count > 1;
            return matches;
        }
    }

    /// <summary>Pushes a return frame; false when five frames are already open.</summary>
    public bool TryPushReturn(GuidanceReturnFrame frame)
    {
        lock (_gate)
        {
            if (_returnStack.Count >= MaxReturnFrames)
            {
                return false;
            }

            _returnStack.Push(frame);
            return true;
        }
    }

    public GuidanceReturnFrame? PopReturn()
    {
        lock (_gate)
        {
            return _returnStack.Count > 0 ? _returnStack.Pop() : null;
        }
    }

    private static bool TryOrdinal(string reference, out int ordinal)
    {
        ordinal = 0;
        var words = reference.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] names = ["first", "second", "third", "fourth", "fifth"];
        for (var i = 0; i < names.Length; i++)
        {
            if (words.Contains(names[i]))
            {
                ordinal = i + 1;
                return true;
            }
        }

        return false;
    }
}
