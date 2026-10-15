using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Binds an authenticated player ID to the character that represents that player on the host.
/// A remote character reads its movement from the validated network input stream; the host's own
/// character keeps reading local input (its MovementInputSource stays null).
///
/// The world/entity phase is responsible for instantiating the character and calling Register;
/// this type deliberately does not create GameObjects yet.
/// </summary>
public sealed class CoopPlayerRegistry : MonoBehaviour
{
    private readonly Dictionary<int, PlayerScript> byId = new Dictionary<int, PlayerScript>();
    private readonly Dictionary<PlayerScript, int> byPlayer = new Dictionary<PlayerScript, int>();

    private CoopPlayerInputFlow InputFlow => GetComponent<CoopPlayerInputFlow>();

    public int Count => byId.Count;
    public IEnumerable<PlayerScript> Players => byId.Values;
    public static PlayerScript FollowTarget(CharacterStatus follower)
    {
        if (follower.CoopFollowPlayerId != 0 && Mirror.NetworkManager.singleton != null)
        {
            var registry = Mirror.NetworkManager.singleton.GetComponent<CoopPlayerRegistry>();
            if (registry != null && registry.TryGetPlayer(follower.CoopFollowPlayerId, out var actor) && actor != null) return actor;
        }
        return GameSystem.instance.player;
    }
    public void Clear()
    {
        foreach (int id in new List<int>(byId.Keys)) Unregister(id);
    }

    /// <summary>Register the character acting for playerId. Remote players read the input stream.</summary>
    public void Register(int playerId, PlayerScript player, bool remote)
    {
        if (playerId == 0 || player == null) return;
        Unregister(playerId);
        byId[playerId] = player;
        byPlayer[player] = playerId;
        player.CoopPlayerId = playerId;
        player.MovementInputSource = remote ? new CoopRemoteMovementInputSource(playerId, InputFlow) : null;
        Debug.Log("[Coop] Player " + playerId + " character registered; remote=" + remote +
                  "; characters=" + Count);
    }

    public void Unregister(int playerId)
    {
        PlayerScript player;
        if (!byId.TryGetValue(playerId, out player)) return;
        byId.Remove(playerId);
        if (player == null) return;
        byPlayer.Remove(player);
        player.MovementInputSource = null;
        if (player.IsRemotePlayer && Mirror.NetworkServer.active)
        {
            CoopActorContext.Release(player);
            if (player.currentNode != null) player.currentNode.RemoveNPC(player);
            GameSystem.instance.activeCharacters.Remove(player);
            Destroy(player.gameObject);
        }
        Debug.Log("[Coop] Player " + playerId + " character released; characters=" + Count);
    }

    public bool TryGetPlayer(int playerId, out PlayerScript player) => byId.TryGetValue(playerId, out player);

    public int GetPlayerId(PlayerScript player)
    {
        int id;
        return player != null && byPlayer.TryGetValue(player, out id) ? id : 0;
    }
}

/// <summary>Feeds a host-side remote character from the client's validated input samples.</summary>
public sealed class CoopRemoteMovementInputSource : IPlayerMovementInputSource
{
    private readonly int playerId;
    private readonly CoopPlayerInputFlow flow;
    private float nextTrace;

    public CoopRemoteMovementInputSource(int playerId, CoopPlayerInputFlow flow)
    {
        this.playerId = playerId;
        this.flow = flow;
    }

    // The flow already returns neutral input when the newest sample has expired.
    public PlayerMovementInput ReadMovement(PlayerScript player)
    {
        PlayerMovementInput input = flow == null ? default(PlayerMovementInput) : flow.Read(playerId);
        // Opt-in trace: exactly what the host simulation consumes for this remote character.
        if (CoopPlayerInputFlow.InputTrace && UnityEngine.Time.unscaledTime >= nextTrace)
        {
            nextTrace = UnityEngine.Time.unscaledTime + 1f;
            bool obey = player != null && player.currentAI != null && player.currentAI.ObeyPlayerInput();
            UnityEngine.Debug.Log("[CoopInputSim] player=" + playerId + "; strafe=" + input.strafe.ToString("F2") +
                "; forward=" + input.forward.ToString("F2") + "; run=" + input.run + "; obey=" + obey +
                "; autopilot=" + (GameSystem.settings.autopilotHuman ? 1 : 0) + (GameSystem.settings.autopilotActive ? 1 : 0) +
                (GameSystem.settings.autopilotPassive ? 1 : 0) + (GameSystem.settings.autopilotAutoFacing ? 1 : 0) +
                "; paused=" + GameSystem.instance.pauseAllInteraction + "/" + GameSystem.instance.pauseActionOnly +
                "; session=" + CoopUiBridge.HasSession(playerId) + "/" + CoopDialogue.HasPending(playerId) +
                "; pos=" + (player == null ? "null" : player.transform.position.ToString("F2")));
        }
        return input;
    }
}
