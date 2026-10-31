using System.Collections.Concurrent;
using OpenAI.Chat;

namespace ProjectManagerAgent.Services;

public class Session
{
    public Session(string id)
    {
        Id = id;
    }

    public string Id { get; }
    public List<ChatMessage> History { get; } = new();
    public SemaphoreSlim Lock { get; } = new (1, 1);
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    public int Turn { get; set; }
    public Dictionary<int, int> PendingDeletes { get; } = new();
}

public class SessionStore
{
    private static readonly TimeSpan MaxIdle = TimeSpan.FromHours(2);
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    public Session Create()
    {
        Prune();
        var session = new Session(Guid.NewGuid().ToString("N"));
        _sessions[session.Id] = session;
        return session;
    }

    public Session? Get(string? id)
    {
        if (id != null && _sessions.TryGetValue(id, out var session))
        {
            session.LastUsed = DateTime.UtcNow;
            return session;
        }
        return null;
    }

     private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _sessions)
        {
            if (now - pair.Value.LastUsed > MaxIdle)
            {
                _sessions.TryRemove(pair.Key, out _);
            }
        }
    }
}