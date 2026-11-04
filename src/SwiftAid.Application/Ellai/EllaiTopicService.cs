using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Topic candidate with its entity link, source version and
/// checkpoint linkage.</summary>
public sealed record EllaiTopic(
    string TopicId,
    string Title,
    IReadOnlyList<string> Aliases,
    string? EntityRef,
    string? EntityVersion,
    string CoverageState,
    string? CheckpointId);

/// <summary>Guidance detour request scoped to one objective.</summary>
public sealed record GuidanceDetour(
    string DetourId,
    string ObjectiveId,
    string Reason,
    int StepLimit,
    DateTimeOffset RequestedAtUtc,
    string? ResumeObjectiveId = null);

/// <summary>Session topic ledger with detour/return-frame guards (ELL-T09
/// groundwork). Topic history is session-scoped and durable through the
/// attached topic store; re-tracking the same title updates one stable row.
/// Frame/detour limits stay in force regardless of topic pressure.</summary>
public sealed class EllaiTopicService
{
    public const int MaxDetours = 5;
    public const int MaxReturnFrames = 5;

    private readonly IEllaiTopicStore _topicStore;
    private readonly Stack<GuidanceDetour> _detours = new();
    private readonly Stack<GuidanceReturnFrame> _returns = new();
    private int _secondTurnReads;

    public EllaiTopicService()
        : this(new InMemoryEllaiTopicStore())
    {
    }

    public EllaiTopicService(IEllaiTopicStore topicStore)
    {
        _topicStore = topicStore ?? throw new ArgumentNullException(nameof(topicStore));
    }

    public bool UsesStore(IEllaiTopicStore? store) => store is not null && ReferenceEquals(_topicStore, store);

    /// <summary>Tracks a topic candidate for the session into durable
    /// history. Re-tracking the same title updates one stable row.</summary>
    public async Task<EllaiTopic> TrackAsync(
        string sessionId,
        string title,
        IReadOnlyList<string> aliases,
        string? entityRef,
        string? entityVersion,
        string? checkpointId = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(aliases);
        var entry = await _topicStore.RecordAsync(sessionId, title, aliases, entityRef,
            entityVersion, checkpointId, now ?? DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        return new EllaiTopic(entry.TopicId, entry.Title, entry.Aliases, entry.EntityRef,
            entry.EntityVersion, entry.CoverageState, entry.CheckpointId);
    }

    /// <summary>Resolves a natural reference against the session's durable
    /// topic history. Ordered mentions ("first", "second") resolve before
    /// semantic search; semantic search returns every match with ambiguity
    /// flagged instead of an arbitrary confident selection.</summary>
    public async Task<(IReadOnlyList<EllaiTopic> Matches, bool Ambiguous)> ResolveAsync(
        string sessionId, string reference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return (Array.Empty<EllaiTopic>(), false);
        }

        var entries = await _topicStore.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var topics = entries.Select(entry => new EllaiTopic(entry.TopicId, entry.Title,
            entry.Aliases, entry.EntityRef, entry.EntityVersion, entry.CoverageState,
            entry.CheckpointId)).ToList();

        if (TryOrdinal(reference, out var ordinal) && ordinal >= 1 && ordinal <= topics.Count)
        {
            return (new[] { topics[ordinal - 1] }, false);
        }

        var matches = topics.Where(t => TitleOrAliasMatches(t, reference)).ToList();
        return (matches, matches.Count > 1);
    }

    public Task<EllaiTopicEntry?> GetAsync(string sessionId, string topicId, CancellationToken ct) =>
        _topicStore.GetAsync(sessionId, topicId, ct);

    public Task<EllaiTopicEntry?> GetByTitleAsync(string sessionId, string title, CancellationToken ct) =>
        _topicStore.GetAsync(sessionId, InMemoryEllaiTopicStore.StableTopicId(sessionId, title), ct);

    public Task<bool> TryCreateAsync(string sessionId, string title, IReadOnlyList<string> aliases,
        string entityRef, string entityVersion, CancellationToken ct) =>
        _topicStore.TryCreateAsync(sessionId, title, aliases, entityRef, entityVersion, DateTimeOffset.UtcNow, ct);

    public Task<bool> CompareExchangePayloadAsync(string sessionId, string topicId, string? expectedRef,
        string? expectedVersion, string newRef, string newVersion, CancellationToken ct) =>
        _topicStore.CompareExchangePayloadAsync(sessionId, topicId, expectedRef, expectedVersion,
            newRef, newVersion, DateTimeOffset.UtcNow, ct);

    private static bool TitleOrAliasMatches(EllaiTopic topic, string reference) =>
        topic.Title.Contains(reference, StringComparison.OrdinalIgnoreCase)
        || topic.Aliases.Any(a => a.Contains(reference, StringComparison.OrdinalIgnoreCase));

    private static readonly IReadOnlyDictionary<string, int> Ordinals =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["first"] = 1,
            ["second"] = 2,
            ["third"] = 3,
            ["1st"] = 1,
            ["2nd"] = 2,
            ["3rd"] = 3
        };

    private static bool TryOrdinal(string reference, out int ordinal)
    {
        foreach (var word in reference.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Ordinals.TryGetValue(word.Trim(',', '.', ';', ':'), out ordinal))
            {
                return true;
            }
        }

        ordinal = 0;
        return false;
    }

    public bool TryPushDetour(GuidanceDetour detour)
    {
        ArgumentNullException.ThrowIfNull(detour);
        if (_detours.Count >= MaxDetours)
        {
            return false;
        }

        _detours.Push(detour);
        return true;
    }

    public GuidanceDetour? PopDetour() => _detours.Count == 0 ? null : _detours.Pop();

    public bool TryPushReturn(GuidanceReturnFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_returns.Count >= MaxReturnFrames)
        {
            return false;
        }

        _returns.Push(frame);
        return true;
    }

    public GuidanceReturnFrame? PopReturn() => _returns.Count == 0 ? null : _returns.Pop();

    /// <summary>Second-turn continuation reads the authoritative turn-1
    /// outcome before reasoning. The model waits for accepted facts; it
    /// does not narrate progress for them.</summary>
    public bool BeginSecondTurnRead() => Interlocked.Increment(ref _secondTurnReads) == 1;
}
