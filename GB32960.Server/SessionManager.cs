using System.Collections.Concurrent;
using System.Net.Sockets;
using GB32960.Protocol;

namespace GB32960.Server;

public class SessionInfo
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public Socket Socket { get; set; } = null!;
    public string VIN { get; set; } = string.Empty;
    public DateTime ConnectTime { get; set; } = DateTime.Now;
    public DateTime LastActiveTime { get; set; } = DateTime.Now;
    public bool IsLoggedIn { get; set; }
    public long ReceivedMessages { get; set; }
    public long SentMessages { get; set; }
    public GB32960MessageBuffer MessageBuffer { get; set; } = new();
    public string ICCID { get; set; } = string.Empty;
    public ushort LoginSequence { get; set; }
}

public class SessionManager
{
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _vinToSession = new();

    public SessionInfo AddSession(Socket socket)
    {
        var session = new SessionInfo { Socket = socket };
        _sessions.TryAdd(session.SessionId, session);
        return session;
    }

    public void RemoveSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            if (!string.IsNullOrEmpty(session.VIN))
                _vinToSession.TryRemove(session.VIN, out _);

            try { session.Socket.Shutdown(SocketShutdown.Both); } catch { }
            try { session.Socket.Close(); } catch { }
        }
    }

    public SessionInfo? GetSession(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) ? s : null;

    public SessionInfo? GetSessionByVin(string vin) =>
        _vinToSession.TryGetValue(vin, out var sid) ? GetSession(sid) : null;

    public void BindVin(string sessionId, string vin)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            // 如果VIN已绑定旧会话，移除旧的
            if (_vinToSession.TryGetValue(vin, out var oldSid) && oldSid != sessionId)
            {
                RemoveSession(oldSid);
            }
            session.VIN = vin;
            _vinToSession[vin] = sessionId;
        }
    }

    public void UpdateActiveTime(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            session.LastActiveTime = DateTime.Now;
    }

    public IEnumerable<SessionInfo> GetAllSessions() => _sessions.Values;
    public int OnlineCount => _sessions.Count;
    public int LoggedInCount => _sessions.Values.Count(s => s.IsLoggedIn);

    public int CleanupTimeoutSessions(int timeoutMinutes)
    {
        int count = 0;
        var cutoff = DateTime.Now.AddMinutes(-timeoutMinutes);
        foreach (var kv in _sessions)
        {
            if (kv.Value.LastActiveTime < cutoff)
            {
                RemoveSession(kv.Key);
                count++;
            }
        }
        return count;
    }
}
