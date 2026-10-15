using System.Linq;
using Mirror;
using UnityEngine;

public struct CoopSoundMessage : NetworkMessage { public string entity; public string sound; public string customType; }
public struct CoopMusicMessage : NetworkMessage { public string song; public string customType; public bool custom; }

public sealed class CoopAudio : MonoBehaviour
{
    public static CoopAudio Instance { get; private set; }
    private void Awake() { Instance = this; }
    public void StartClient()
    {
        NetworkClient.RegisterHandler<CoopSoundMessage>(Sound);
        NetworkClient.RegisterHandler<CoopMusicMessage>(Music);
    }
    public void StopClient()
    {
        NetworkClient.UnregisterHandler<CoopSoundMessage>(); NetworkClient.UnregisterHandler<CoopMusicMessage>();
    }
    public static void PublishSound(CharacterStatus actor, string sound, NPCType customType)
    {
        if (!NetworkServer.active || Instance == null || string.IsNullOrEmpty(sound)) return;
        string entity = Instance.GetComponent<CoopReplication>().ReferenceKey(actor);
        if (entity == null) return;
        var message = new CoopSoundMessage { entity = entity, sound = sound, customType = customType?.name };
        foreach (var connection in NetworkServer.connections.Values)
            if (connection != null && connection.isAuthenticated && connection != NetworkServer.localConnection) connection.Send(message);
    }
    public static bool RouteMusic(string song, NPCType customType)
    {
        var actor = CoopActorContext.Current;
        if (actor == null || !actor.IsRemotePlayer || !NetworkServer.active || Instance == null) return false;
        if (Instance.GetComponent<CoopSession>().TryGetConnection(actor.CoopPlayerId, out var connection))
            connection.Send(new CoopMusicMessage { song = song, customType = customType?.name });
        return true;
    }
    public static bool RouteMusic(string song, bool custom)
    {
        var actor = CoopActorContext.Current;
        if (actor == null || !actor.IsRemotePlayer || !NetworkServer.active || Instance == null) return false;
        if (Instance.GetComponent<CoopSession>().TryGetConnection(actor.CoopPlayerId, out var connection))
            connection.Send(new CoopMusicMessage { song = song, custom = custom });
        return true;
    }
    private void Sound(CoopSoundMessage message)
    {
        if (NetworkServer.active || !CoopReplication.IsReplica || string.IsNullOrEmpty(message.entity)) return;
        if (GetComponent<CoopReplication>().ResolveReference(message.entity) is CharacterStatus actor)
        {
            NPCType type = message.customType == null ? null : NPCType.types.FirstOrDefault(t => t.name == message.customType);
            actor.PlaySound(message.sound, type);
        }
    }
    private void Music(CoopMusicMessage message)
    {
        if (NetworkServer.active || !CoopReplication.IsReplica) return;
        NPCType type = message.customType == null ? null : NPCType.types.FirstOrDefault(t => t.name == message.customType);
        if (message.customType == null) GameSystem.instance.PlayMusic(message.song, message.custom);
        else GameSystem.instance.PlayMusic(message.song, type);
    }
}
