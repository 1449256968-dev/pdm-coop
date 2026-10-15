using System;
using Mirror;
using Newtonsoft.Json;
using UnityEngine;

public struct CoopGameLogMessage : NetworkMessage
{
    public string text, replacements;
    public int room;
    public float r, g, b, a;
}

// Preserve the original room visibility filter for each player's message log.
public sealed class CoopGameLog : MonoBehaviour
{
    public static int RoomFilterDepth;
    public void StartClient() { NetworkClient.RegisterHandler<CoopGameLogMessage>(Receive); }
    public void StopClient() { NetworkClient.UnregisterHandler<CoopGameLogMessage>(); }
    public static void Publish(string text, PathNode source, SaveableColour colour, System.Collections.Generic.Dictionary<string, string> replacements)
    {
        if (!NetworkServer.active || RoomFilterDepth != 0 || NetworkManager.singleton == null) return;
        var manager = NetworkManager.singleton;
        int room = source == null ? -1 : CoopTopology.Rooms(GameSystem.instance.map).IndexOf(source.associatedRoom);
        Color tint = (colour ?? GameSystem.settings.neutralColour).GetColour();
        var message = new CoopGameLogMessage { text = text, room = room, r = tint.r, g = tint.g, b = tint.b, a = tint.a,
            replacements = replacements == null ? null : JsonConvert.SerializeObject(replacements) };
        var actor = CoopActorContext.Current;
        foreach (var connection in NetworkServer.connections.Values)
        {
            if (!connection.isAuthenticated || connection == NetworkServer.localConnection) continue;
            if (source == null && actor != null && actor.IsRemotePlayer && manager.GetComponent<CoopSession>().GetPlayerId(connection) != actor.CoopPlayerId) continue;
            connection.Send(message);
        }
    }
    private void Receive(CoopGameLogMessage message)
    {
        if (NetworkServer.active || !CoopReplication.IsReplica) return;
        var game = GameSystem.instance;
        var tint = new SaveableColour(new Color(message.r, message.g, message.b, message.a));
        var replacements = message.replacements == null ? null : JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, string>>(message.replacements);
        if (message.room < 0) game.LogMessage(message.text, tint, replacements);
        else
        {
            var rooms = CoopTopology.Rooms(game.map);
            if (message.room < rooms.Count && rooms[message.room].pathNodes.Count > 0)
                game.LogMessage(message.text, rooms[message.room].pathNodes[0], tint, replacements);
        }
    }
}
