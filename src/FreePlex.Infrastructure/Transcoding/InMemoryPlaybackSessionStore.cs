using System.Collections.Concurrent;
using FreePlex.Application.Abstractions;

namespace FreePlex.Infrastructure.Transcoding;

public sealed class InMemoryPlaybackSessionStore : IPlaybackSessionStore
{
    private readonly ConcurrentDictionary<string, PlaybackSession> _sessions = new();

    public PlaybackSession Create(PlaybackSession session)
    {
        _sessions[session.SessionId] = session;
        return session;
    }

    public PlaybackSession? Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) ? s : null;

    public void Touch(string sessionId, DateTimeOffset newExpiry)
    {
        if (_sessions.TryGetValue(sessionId, out var s))
            s.ExpiresAt = newExpiry;
    }

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

    public IReadOnlyCollection<PlaybackSession> ActiveSessions => _sessions.Values.ToList();
}
