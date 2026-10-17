using kcp2k;

// Read-only queue feedback; KCP's congestion and disconnect policy stay intact.
public sealed class CoopKcpTransport : KcpTransport
{
    public int PendingReliable(int connectionId) => server != null &&
        server.connections.TryGetValue(connectionId, out KcpServerConnection connection)
        ? connection.SendQueueCount + connection.SendBufferCount : 0;
}
