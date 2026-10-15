using UnityEngine;
using kcp2k;

/// <summary>Creates the connection manager without requiring edits to recovered scenes.</summary>
public static class CoopNetworkBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateNetworkManager()
    {
        if (Mirror.NetworkManager.singleton != null)
            return;

        var networkObject = new GameObject("Coop Network Manager");
        Object.DontDestroyOnLoad(networkObject);
        networkObject.AddComponent<KcpTransport>();
        var manager = networkObject.AddComponent<CoopNetworkManager>();
        networkObject.AddComponent<CoopConnectionPanel>();
        StartFromCommandLine(manager);
    }

    // Optional direct-connect entry for LAN/internet sessions and scripted validation:
    //   -coopHost 7777             start a room on UDP 7777
    //   -coopJoin 10.0.0.5:7777    join a host
    private static void StartFromCommandLine(CoopNetworkManager manager)
    {
        string[] arguments = System.Environment.GetCommandLineArgs();
        string host = ValueAfter(arguments, "-coopHost");
        if (host != null && ushort.TryParse(host, out ushort port) && port > 0)
        {
            Debug.Log("[Coop] Command line: hosting UDP " + port);
            manager.StartHostOnPort(port);
            if (HasFlag(arguments, "-coopStartGame"))
            {
                var spawner = manager.GetComponent<CoopRemoteAvatarSpawner>();
                if (spawner != null)
                {
                    spawner.AutoStartGame = true;
                    Debug.Log("[Coop] Command line: the shared game will start automatically.");
                }
            }
            return;
        }
        string target = ValueAfter(arguments, "-coopJoin");
        if (target == null) return;
        int separator = target.LastIndexOf(':');
        if (separator <= 0 || separator == target.Length - 1) return;
        string address = target.Substring(0, separator);
        if (!ushort.TryParse(target.Substring(separator + 1), out ushort joinPort) || joinPort == 0) return;
        Debug.Log("[Coop] Command line: joining " + address + ":" + joinPort);
        manager.JoinAddress(address, joinPort);
    }

    private static string ValueAfter(string[] arguments, string name)
    {
        for (int i = 0; i < arguments.Length - 1; i++)
            if (string.Equals(arguments[i], name, System.StringComparison.OrdinalIgnoreCase))
                return arguments[i + 1];
        return null;
    }

    private static bool HasFlag(string[] arguments, string name)
    {
        foreach (string argument in arguments)
            if (string.Equals(argument, name, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
