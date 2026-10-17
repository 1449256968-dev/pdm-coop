using System;
using Mirror;
using UnityEngine;
using kcp2k;

/// <summary>Direct-IP sessions with host-authoritative world, actor and interaction replication.</summary>
public sealed class CoopNetworkManager : NetworkManager
{
    public const int MaxPlayersIncludingHost = 8;
    public const ushort DefaultPort = 7777;
    // v2 requires version authentication before Mirror Ready.
    public const int ProtocolVersion = 20;
    public const string GameBuild = "Fork0.7.1";

    public KcpTransport KcpTransport { get; private set; }
    public string StatusText { get; private set; } = "未连接";
    private string lastFailureReason;

    public bool ClientVerified => NetworkClient.isConnected && NetworkClient.connection != null &&
                                  NetworkClient.connection.isAuthenticated;
    public int VerifiedPlayers
    {
        get
        {
            int count = 0;
            foreach (var connection in NetworkServer.connections.Values)
                if (connection.isAuthenticated)
                    count++;
            return count;
        }
    }

    public override void Awake()
    {
        CoopActorStatics.Initialize();
        autoCreatePlayer = false;
        maxConnections = MaxPlayersIncludingHost;
        DontDestroyOnLoad(gameObject);
        KcpTransport = GetComponent<KcpTransport>();
        transport = KcpTransport;
        authenticator = GetComponent<CoopVersionAuthenticator>() ?? gameObject.AddComponent<CoopVersionAuthenticator>();
        gameObject.AddComponent<CoopRoomSettings>();
        gameObject.AddComponent<CoopSession>();
        gameObject.AddComponent<CoopPlayerSelection>();
        gameObject.AddComponent<CoopNameplates>();
        gameObject.AddComponent<CoopObserverPlayerView>();
        gameObject.AddComponent<CoopBillboards>();
        gameObject.AddComponent<CoopPlayerInputFlow>();
        gameObject.AddComponent<CoopPlayerRegistry>();
        gameObject.AddComponent<CoopRemoteAvatarSpawner>();
        gameObject.AddComponent<CoopWorld>();
        gameObject.AddComponent<CoopReplication>();
        gameObject.AddComponent<CoopMotion>();
        gameObject.AddComponent<CoopPrediction>();
        gameObject.AddComponent<CoopActions>();
        gameObject.AddComponent<CoopReplicaHands>();
        gameObject.AddComponent<CoopAttackEffects>();
        gameObject.AddComponent<CoopDialogue>();
        gameObject.AddComponent<CoopInventory>();
        gameObject.AddComponent<CoopUiBridge>();
        gameObject.AddComponent<CoopAudio>();
        base.Awake();
        gameObject.AddComponent<CoopGameLog>();
        if (KcpTransport != null)
            KcpTransport.port = DefaultPort;
    }

    public bool StartHostOnPort(ushort port)
    {
        if (KcpTransport == null || isNetworkActive || port == 0)
            return false;
        KcpTransport.port = port;
        maxConnections = MaxPlayersIncludingHost;
        lastFailureReason = null;
        StatusText = "正在创建房间……";
        StartHost();
        return true;
    }

    public bool JoinAddress(string address, ushort port)
    {
        if (KcpTransport == null || isNetworkActive || port == 0 || string.IsNullOrWhiteSpace(address))
            return false;
        if (!CoopEndpoint.TryParse(address, port.ToString(), out string host, out port, out string error))
        {
            StatusText = error;
            return false;
        }
        KcpTransport.port = port;
        networkAddress = host;
        lastFailureReason = null;
        StatusText = "正在连接并校验版本……";
        GetComponent<CoopPlayerSelection>().CaptureLocal();
        GetComponent<CoopRoomSettings>().BeginClient();
        StartClient();
        return true;
    }

    public void SetConnectionStatus(string reason) => StatusText = reason;

    public void SetConnectionFailure(string reason)
    {
        lastFailureReason = reason;
        StatusText = reason;
    }

    public override void OnStartServer()
    {
        // The host also runs the authoritative simulation for every player and NPC, plus a
        // snapshot capture every ~100ms, so its frame time sits just above the 16.67ms budget
        // under real play. The shipped Ultra/High quality levels enable VSync, which then misses
        // every vblank and drops the renderer to exactly 30 FPS. A host runs unsynced and capped
        // at 60 so a missed budget costs one dropped frame instead of halving the frame rate.
        // Clients keep their own presentation settings untouched.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
        base.OnStartServer();
        GetComponent<CoopRoomSettings>().StartServer();
        GetComponent<CoopSession>().StartServer();
        GetComponent<CoopPlayerSelection>().StartServer();
        GetComponent<CoopPlayerInputFlow>().StartServer();
        GetComponent<CoopWorld>().StartServer();
        GetComponent<CoopActions>().StartServer();
        GetComponent<CoopDialogue>().StartServer();
        GetComponent<CoopInventory>().StartServer();
        GetComponent<CoopUiBridge>().StartServer();
        StatusText = "房间已建立，等待加入者。";
    }

    public override void OnStartClient()
    {
        GetComponent<CoopSession>().StartClient();
        GetComponent<CoopRoomSettings>().StartClient();
        GetComponent<CoopPlayerInputFlow>().StartClient();
        GetComponent<CoopWorld>().StartClient();
        GetComponent<CoopReplication>().StartClient();
        GetComponent<CoopMotion>().StartClient();
        GetComponent<CoopPrediction>().StartClient();
        GetComponent<CoopAttackEffects>().StartClient();
        GetComponent<CoopDialogue>().StartClient();
        GetComponent<CoopUiBridge>().StartClient();
        GetComponent<CoopAudio>().StartClient();
        GetComponent<CoopGameLog>().StartClient();
    }

    public override void OnServerConnect(NetworkConnectionToClient connection)
    {
        StatusText = "房间已建立；由房主同步世界和角色状态。";
        Debug.Log("[Coop] Server authenticated; connectionId=" + connection.connectionId +
                  "; local=" + (connection == NetworkServer.localConnection) + "; verifiedPlayers=" + VerifiedPlayers);
        base.OnServerConnect(connection);
        GetComponent<CoopRoomSettings>().SendTo(connection);
        GetComponent<CoopWorld>().SendTo(connection);
    }

    public override void OnClientConnect()
    {
        base.OnClientConnect();
        GetComponent<CoopPlayerSelection>().SendLocal();
        Debug.Log("[Coop] Client authenticated; ready=" + NetworkClient.ready);
    }

    public override void OnServerDisconnect(NetworkConnectionToClient connection)
    {
        (authenticator as CoopVersionAuthenticator)?.Forget(connection);
        GetComponent<CoopReplication>().ReleaseConnection(connection);
        GetComponent<CoopPlayerSelection>().Release(connection);
        var session = GetComponent<CoopSession>();
        var input = GetComponent<CoopPlayerInputFlow>();
        int playerId = session.GetPlayerId(connection);
        if (playerId != 0)
        {
            input.Release(playerId);
            GetComponent<CoopActions>().Release(playerId);
            GetComponent<CoopDialogue>().Release(playerId);
            GetComponent<CoopInventory>().Release(playerId);
            GetComponent<CoopUiBridge>().Release(playerId);
            GetComponent<CoopPlayerRegistry>().Unregister(playerId);
        }
        session.Release(connection);
        Debug.Log("[Coop] Server disconnected; connectionId=" + connection.connectionId);
        base.OnServerDisconnect(connection);
    }

    public override void OnClientDisconnect()
    {
        GetComponent<CoopSession>().StopClient();
        GetComponent<CoopRoomSettings>().EndClient();
        GetComponent<CoopPlayerInputFlow>().StopClient();
        GetComponent<CoopReplication>().StopClient();
        GetComponent<CoopMotion>().StopClient();
        GetComponent<CoopPrediction>().StopClient();
        GetComponent<CoopAttackEffects>().StopClient();
        CoopFirstPersonVisibility.RestoreLocal();
        GetComponent<CoopDialogue>().StopClient();
        GetComponent<CoopUiBridge>().StopClient();
        GetComponent<CoopAudio>().StopClient();
        GetComponent<CoopGameLog>().StopClient();
        GetComponent<CoopWorld>().StopClient();
        if (!NetworkServer.active)
            StatusText = string.IsNullOrEmpty(lastFailureReason) ? "连接已断开。" : lastFailureReason;
        Debug.Log("[Coop] Client disconnected.");
        base.OnClientDisconnect();
    }

    public override void OnClientError(TransportError error, string reason)
    {
        SetConnectionFailure("连接错误：" + error + " " + reason);
        Debug.LogError("[Coop] Client error: " + error + " " + reason);
    }

    public override void OnClientTransportException(Exception exception)
    {
        SetConnectionFailure("连接异常：" + exception.Message);
        Debug.LogException(exception);
    }

    public override void OnStopServer()
    {
        GetComponent<CoopPrediction>().ResetServer();
        GetComponent<CoopSession>().StopServer();
        GetComponent<CoopPlayerSelection>().StopServer();
        GetComponent<CoopPlayerInputFlow>().StopServer();
        GetComponent<CoopWorld>().StopServer();
        GetComponent<CoopReplication>().StopServer();
        GetComponent<CoopMotion>().Reset();
        CoopFirstPersonVisibility.RestoreLocal();
        GetComponent<CoopActions>().StopServer();
        GetComponent<CoopDialogue>().StopServer();
        GetComponent<CoopInventory>().StopServer();
        GetComponent<CoopUiBridge>().StopServer();
        base.OnStopServer();
    }

    public override void OnStopHost()
    {
        StatusText = "未连接";
        base.OnStopHost();
    }
}
