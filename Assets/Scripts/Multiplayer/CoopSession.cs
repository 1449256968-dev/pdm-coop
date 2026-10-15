using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;

/// <summary>
/// Room-scoped player identity and entity lifecycle IDs, owned by the host.
/// The host is the first authenticated connection and becomes player 1; every joining
/// connection receives the next monotonic player ID after version authentication.
/// IDs are never reused within a room session, so a reconnect gets a fresh identity.
/// </summary>
public sealed class CoopSession : MonoBehaviour
{
    private readonly Dictionary<int, int> connectionToPlayer = new Dictionary<int, int>();
    private readonly Dictionary<int, int> playerToConnection = new Dictionary<int, int>();
    private int nextPlayerId = 1;
    private long nextEntityId = 1;
    private readonly Dictionary<int, string> names = new Dictionary<int, string>();
    public IReadOnlyDictionary<int, string> Names => names;
    public void StartClient() => NetworkClient.RegisterHandler<CoopPlayerNamesMessage>(message => {
        if (NetworkServer.active) return;
        names.Clear();
        for (int i = 0; i < message.ids.Length; i++) names[message.ids[i]] = message.names[i];
        Debug.Log("[CoopNames] roster=" + string.Join(",", names.Select(p => p.Key + ":" + p.Value)));
    });
    public void StopClient() { if (!NetworkServer.active) names.Clear(); }
    private void SendNames()
    {
        var message = new CoopPlayerNamesMessage { ids = names.Keys.ToArray(), names = names.Values.ToArray() };
        foreach (var connection in NetworkServer.connections.Values)
            if (connection.isAuthenticated) connection.Send(message);
    }

    public bool IsServer => NetworkServer.active;
    public int PlayerCount => playerToConnection.Count;

    public void StartServer()
    {
        connectionToPlayer.Clear();
        playerToConnection.Clear();
        nextPlayerId = 1;
        names.Clear();
        nextEntityId = 1;
    }

    public void StopServer()
    {
        names.Clear();
        connectionToPlayer.Clear();
        playerToConnection.Clear();
    }

    /// <summary>Player ID bound to this connection, or 0 when it is not yet authenticated/assigned.</summary>
    public int GetPlayerId(NetworkConnectionToClient connection)
    {
        if (connection == null) return 0;
        int id;
        return connectionToPlayer.TryGetValue(connection.connectionId, out id) ? id : 0;
    }

    public bool TryGetConnection(int playerId, out NetworkConnectionToClient connection)
    {
        int connectionId;
        if (!playerToConnection.TryGetValue(playerId, out connectionId) ||
            !NetworkServer.connections.TryGetValue(connectionId, out connection))
        {
            connection = null;
            return false;
        }
        return connection != null;
    }

    /// <summary>Monotonic, room-scoped ID for a replicated world entity. Never zero.</summary>
    public long AllocateEntityId() => nextEntityId++;

    /// <summary>Assign the next player ID to an authenticated connection. Idempotent.</summary>
    public void Assign(NetworkConnectionToClient connection, string username = null)
    {
        if (connection == null || connectionToPlayer.ContainsKey(connection.connectionId)) return;
        int id = nextPlayerId++;
        connectionToPlayer[connection.connectionId] = id;
        playerToConnection[id] = connection.connectionId;
        string clean = CoopUsername.Clean(username);
        names[id] = string.IsNullOrEmpty(clean) ? "玩家 " + id : clean;
        SendNames();
        Debug.Log("[Coop] Player " + id + " bound; connectionId=" + connection.connectionId +
                  "; local=" + (connection == NetworkServer.localConnection) + "; players=" + PlayerCount);
    }

    public void Release(NetworkConnectionToClient connection)
    {
        if (connection == null) return;
        int connectionId = connection.connectionId;
        int playerId;
        if (!connectionToPlayer.TryGetValue(connectionId, out playerId)) return;
        connectionToPlayer.Remove(connectionId);
        playerToConnection.Remove(playerId);
        names.Remove(playerId);
        SendNames();
        Debug.Log("[Coop] Player " + playerId + " released; connectionId=" + connectionId + "; players=" + PlayerCount);
    }
}
