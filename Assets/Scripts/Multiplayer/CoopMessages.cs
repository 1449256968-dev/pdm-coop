using Mirror;

public struct CoopHelloMessage : NetworkMessage
{
    public int protocolVersion;
    public string gameBuild;
    public string username;
}

public struct CoopPlayerNamesMessage : NetworkMessage { public int[] ids; public string[] names; }

public struct CoopWelcomeMessage : NetworkMessage
{
    public bool accepted;
    public string reason;
    public int maxPlayers;
}
