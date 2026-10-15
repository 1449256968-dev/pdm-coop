using System.Collections;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

/// <summary>Version gate before Mirror accepts Ready or gameplay messages.</summary>
public sealed class CoopVersionAuthenticator : NetworkAuthenticator
{
    private const float HandshakeTimeout = 10f;
    private readonly HashSet<NetworkConnectionToClient> pending = new HashSet<NetworkConnectionToClient>();
    private bool awaitingWelcome;
    private CoopNetworkManager Manager => GetComponent<CoopNetworkManager>();

    public override void OnStartServer() => NetworkServer.RegisterHandler<CoopHelloMessage>(OnHello, false);

    public override void OnStopServer()
    {
        NetworkServer.UnregisterHandler<CoopHelloMessage>();
        pending.Clear();
        StopAllCoroutines();
    }

    public override void OnStartClient() => NetworkClient.RegisterHandler<CoopWelcomeMessage>(OnWelcome, false);

    public override void OnStopClient()
    {
        NetworkClient.UnregisterHandler<CoopWelcomeMessage>();
        awaitingWelcome = false;
    }

    public override void OnServerAuthenticate(NetworkConnectionToClient connection)
    {
        pending.Add(connection);
        StartCoroutine(WaitForHello(connection));
    }

    public override void OnClientAuthenticate()
    {
        awaitingWelcome = true;
        Manager.SetConnectionStatus("传输已连接，正在校验版本……");
        Debug.Log("[Coop] Client transport connected; sending hello.");
        NetworkClient.Send(new CoopHelloMessage
        {
            protocolVersion = CoopNetworkManager.ProtocolVersion,
            gameBuild = CoopNetworkManager.GameBuild,
            username = CoopUsername.Local
        });
        StartCoroutine(WaitForWelcome(NetworkClient.connection));
    }

    private void OnHello(NetworkConnectionToClient connection, CoopHelloMessage message)
    {
        if (!pending.Remove(connection))
            return;
        bool compatible = message.protocolVersion == CoopNetworkManager.ProtocolVersion &&
                          message.gameBuild == CoopNetworkManager.GameBuild;
        Debug.Log("[Coop] Server received hello; connectionId=" + connection.connectionId +
                  "; local=" + (connection == NetworkServer.localConnection) + "; compatible=" + compatible);
        connection.Send(new CoopWelcomeMessage
        {
            accepted = compatible,
            reason = compatible ? "连接已验证；世界和角色状态由房主同步。" : "游戏或联机协议版本不一致。",
            maxPlayers = CoopNetworkManager.MaxPlayersIncludingHost
        });
        if (compatible)
        {
            ServerAccept(connection);
            GetComponent<CoopSession>().Assign(connection, message.username);
        }
        else
            StartCoroutine(RejectAfterReply(connection));
    }

    private void OnWelcome(CoopWelcomeMessage message)
    {
        if (!awaitingWelcome)
            return;
        awaitingWelcome = false;
        Debug.Log("[Coop] Client received welcome; accepted=" + message.accepted);
        if (message.accepted)
        {
            Manager.SetConnectionStatus(message.reason);
            ClientAccept();
        }
        else
        {
            Manager.SetConnectionFailure(message.reason);
            ClientReject();
        }
    }

    private IEnumerator WaitForHello(NetworkConnectionToClient connection)
    {
        yield return new WaitForSecondsRealtime(HandshakeTimeout);
        if (pending.Remove(connection))
        {
            Debug.LogWarning("[Coop] Hello timed out; connectionId=" + connection.connectionId);
            ServerReject(connection);
        }
    }

    private IEnumerator WaitForWelcome(NetworkConnection connection)
    {
        yield return new WaitForSecondsRealtime(HandshakeTimeout);
        if (awaitingWelcome && NetworkClient.connection == connection)
        {
            awaitingWelcome = false;
            Manager.SetConnectionFailure("版本校验超时，连接已关闭。");
            ClientReject();
        }
    }

    private IEnumerator RejectAfterReply(NetworkConnectionToClient connection)
    {
        yield return new WaitForSecondsRealtime(0.5f);
        ServerReject(connection);
    }

    public void Forget(NetworkConnectionToClient connection) => pending.Remove(connection);
}
