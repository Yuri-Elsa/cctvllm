using System.Security.Cryptography;

public sealed record SessionInfo(string Id, string User, string Role, DateTimeOffset Created, DateTimeOffset LastSeen, string? Address);

public sealed class SessionStore(TimeSpan? idleLimit = null)
{
    private readonly TimeSpan idle = idleLimit ?? TimeSpan.FromHours(12);
    private readonly Dictionary<string, SessionInfo> sessions = new();
    private readonly object gate = new();

    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public SessionInfo Start(string user, string role, string? address)
    {
        lock (gate)
        {
            var now = Now();
            foreach (var stale in sessions.Where(s => now - s.Value.LastSeen >= idle).Select(s => s.Key).ToList()) sessions.Remove(stale);
            var session = new SessionInfo(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), user, role, now, now, address);
            sessions[session.Id] = session;
            return session;
        }
    }

    public bool IsActive(string? id)
    {
        if (id is null) return false;
        lock (gate)
        {
            if (!sessions.TryGetValue(id, out var session)) return false;
            var now = Now();
            if (now - session.LastSeen >= idle) { sessions.Remove(id); return false; }
            sessions[id] = session with { LastSeen = now };
            return true;
        }
    }

    public bool Revoke(string id) { lock (gate) return sessions.Remove(id); }

    public int RevokeAll(string? exceptId = null)
    {
        lock (gate)
        {
            var ids = sessions.Keys.Where(id => id != exceptId).ToList();
            foreach (var id in ids) sessions.Remove(id);
            return ids.Count;
        }
    }

    public IReadOnlyList<SessionInfo> List() { lock (gate) return sessions.Values.OrderByDescending(s => s.LastSeen).ToList(); }
}