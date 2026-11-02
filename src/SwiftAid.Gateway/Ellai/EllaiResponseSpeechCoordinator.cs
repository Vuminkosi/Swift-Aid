using System.Collections.Generic;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Coordinates cancellation for one synthesized response playback.
/// It deliberately has no connection to session utterances or tool work.</summary>
public sealed class EllaiResponseSpeechCoordinator(TimeProvider? timeProvider = null, EllaiPreparedResponseAudio? preparedAudio = null)
{
    private static readonly TimeSpan CancellationRetention = TimeSpan.FromMinutes(10);
    private const int MaximumCanceledPlaybacks = 4096;
    private readonly object _gate = new();
    private readonly Dictionary<PlaybackKey, PlaybackLease> _active = [];
    private readonly Dictionary<PlaybackKey, DateTimeOffset> _canceled = [];
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public PlaybackLease Begin(string sessionId, long sequence, Guid playbackId, CancellationToken requestAborted)
    {
        var key = new PlaybackKey(sessionId, sequence, playbackId);
        lock (_gate)
        {
            PruneCanceled();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
            if (_canceled.ContainsKey(key))
            {
                cancellation.Cancel();
            }

            var lease = new PlaybackLease(this, key, cancellation);
            if (!cancellation.IsCancellationRequested)
            {
                if (!_active.TryAdd(key, lease))
                {
                    cancellation.Cancel();
                }
            }

            return lease;
        }
    }

    public bool Interrupt(string sessionId, long sequence, Guid playbackId)
    {
        var key = new PlaybackKey(sessionId, sequence, playbackId);
        lock (_gate)
        {
            PruneCanceled();
            if (!_canceled.ContainsKey(key))
            {
                if (_canceled.Count >= MaximumCanceledPlaybacks)
                {
                    var oldest = _canceled.MinBy(pair => pair.Value);
                    if (!oldest.Equals(default(KeyValuePair<PlaybackKey, DateTimeOffset>)))
                    {
                        _canceled.Remove(oldest.Key);
                    }
                }

                _canceled[key] = _timeProvider.GetUtcNow() + CancellationRetention;
            }

            if (!_active.TryGetValue(key, out var lease))
            {
                return false;
            }

            lease.Cancel();
            preparedAudio?.Interrupt(sessionId, sequence);
            return true;
        }
    }

    private void End(PlaybackLease lease)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(lease.Key, out var active) && ReferenceEquals(active, lease))
            {
                _active.Remove(lease.Key);
            }

            lease.DisposeSource();
        }
    }

    private void PruneCanceled()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var key in _canceled.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
        {
            _canceled.Remove(key);
        }
    }

    internal readonly record struct PlaybackKey(string SessionId, long Sequence, Guid PlaybackId);

    public sealed class PlaybackLease : IDisposable
    {
        private readonly EllaiResponseSpeechCoordinator _owner;
        private readonly CancellationTokenSource _cancellation;
        private int _disposed;

        internal PlaybackLease(
            EllaiResponseSpeechCoordinator owner,
            PlaybackKey key,
            CancellationTokenSource cancellation)
        {
            _owner = owner;
            Key = key;
            _cancellation = cancellation;
        }

        internal PlaybackKey Key { get; }
        public CancellationToken Token => _cancellation.Token;

        internal void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        internal void DisposeSource() => _cancellation.Dispose();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.End(this);
            }
        }
    }
}
