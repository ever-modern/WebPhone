using System.Collections.Concurrent;

namespace WebPhone.Backend.Services;

using System.Collections.Concurrent;

public sealed class ConnectedUsersStorage
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>
        _users = new();

    public void Connected(string userId, string connectionId)
    {
        var connections = _users.GetOrAdd(
            userId,
            static _ => new ConcurrentDictionary<string, byte>());

        connections.TryAdd(connectionId, 0);
    }

    public void Disconnected(string userId, string connectionId)
    {
        if (!_users.TryGetValue(userId, out var connections))
            return;

        connections.TryRemove(connectionId, out _);

        if (connections.IsEmpty)
        {
            _users.TryRemove(userId, out _);
        }
    }

    public IReadOnlyCollection<string> Users =>
        _users.Keys.ToArray();

    public IReadOnlyCollection<string> GetConnections(string userId)
    {
        if (!_users.TryGetValue(userId, out var connections))
            return [];

        return connections.Keys.ToArray();
    }
}