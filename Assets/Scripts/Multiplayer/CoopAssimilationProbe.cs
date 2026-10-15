using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;

public struct CoopAssimReady : NetworkMessage { }
public struct CoopAssimCommand : NetworkMessage { public int phase, attacker, victim; public float yaw, pitch; public int[] ids; public string[] types, pictures; }
public struct CoopAssimAck : NetworkMessage { public int phase; public string evidence; }
public struct CoopAssimFinished : NetworkMessage { }

// Explicit opt-in regression. Setup prepares legal original-game prerequisites;
// assimilation itself must come from a real client's normal action input packet.
public sealed class CoopAssimilationProbe : MonoBehaviour
{
    public static bool Completed { get; private set; }
    private static bool Enabled => Environment.GetCommandLineArgs().Contains("-coopAssimilationValidate") &&
        !Environment.GetCommandLineArgs().Contains("-coopMonsterRoster");
    private readonly HashSet<int> ready = new HashSet<int>();
    private readonly HashSet<int> acknowledged = new HashSet<int>();
    private bool registered, readySent, started, finished, sawOriginalTransform;
    private int phase, clientPhase = -1;
    private float began, phaseAt, nextInput;
    private CoopAssimCommand command;
    private PlayerScript attacker, victim;
    private List<PlayerScript> players;
    private PathNode testNode;
    private Vector3 testLocation;
    private bool originalPause, originalVictimPause;
    private Func<PlayerMovementInput> movementSource;
    private Func<CoopActionSample> actionSource;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Enabled) new GameObject("Coop Assimilation Validation").AddComponent<CoopAssimilationProbe>();
    }
    private void Start()
    {
        began = Time.unscaledTime;
        if (Environment.GetCommandLineArgs().Contains("-coopHost"))
        {
            NetworkServer.RegisterHandler<CoopAssimReady>((connection, message) => ready.Add(NetworkManager.singleton.GetComponent<CoopSession>().GetPlayerId(connection)));
            NetworkServer.RegisterHandler<CoopAssimAck>((connection, message) => {
                if (message.phase != phase) return;
                int id = NetworkManager.singleton.GetComponent<CoopSession>().GetPlayerId(connection);
                acknowledged.Add(id); Debug.Log("[CoopAssim] Verified client " + id + ": " + message.evidence);
            });
        }
    }
    private void Update()
    {
        if (!Enabled || GameSystem.instance == null || CoopReplication.Instance == null || finished) return;
        if (Time.unscaledTime - began > 180) { finished = true; throw new InvalidOperationException("Assimilation regression did not finish within 180 seconds"); }
        if (!NetworkServer.active) Client();
    }
    private void Client()
    {
        if (!NetworkClient.isConnected) return;
        if (!registered)
        {
            NetworkClient.RegisterHandler<CoopAssimCommand>(message => { command = message; clientPhase = message.phase; nextInput = 0; });
            NetworkClient.RegisterHandler<CoopAssimFinished>(message => {
                finished = true; Completed = true;
                ReleaseInput();
                Debug.Log("[CoopAssim] CLIENT COMPLETE: host verified all phases; validation input released");
            });
            registered = true;
        }
        int owner = CoopReplication.Instance.LocalPlayerId;
        if (owner == 0) return;
        if (!readySent) { NetworkClient.Send(new CoopAssimReady()); readySent = true; }
        var game = GameSystem.instance;
        if (clientPhase < 0) return;
        if (command.attacker == owner)
        {
            game.player.horizontalPlaneRotation = command.yaw; game.player.CoopViewPitch = command.pitch;
            movementSource = () => new PlayerMovementInput { yaw = command.yaw };
            actionSource = () => {
                uint up = 0;
                if (Time.unscaledTime >= nextInput) { nextInput = Time.unscaledTime + 0.75f; up = 1u << (int)HoPInput.SecondaryAction; }
                return new CoopActionSample { up = up, quickItem = -1 };
            };
        }
        else
        {
            movementSource = () => default(PlayerMovementInput);
            actionSource = () => new CoopActionSample { quickItem = -1 };
        }
        NetworkManager.singleton.GetComponent<CoopPlayerInputFlow>().ValidationInputSource = movementSource;
        CoopActions.Instance.ValidationInputSource = actionSource;
        if (command.types == null || acknowledged.Contains(clientPhase)) return;
        var actors = CoopReplication.Instance.VisibleReplicas<PlayerScript>().Cast<PlayerScript>().ToList();
        foreach (int id in command.ids)
        {
            PlayerScript actor = actors.FirstOrDefault(p => p.CoopPlayerId == id);
            int index = Array.IndexOf(command.ids, id);
            if (actor == null || actor.npcType.name != command.types[index]) return;
            string picture = Picture(actor);
            if (picture != command.pictures[index]) return;
            if (actor != game.player)
            {
                Renderer renderer = actor.mainSpriteRenderer;
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                    (game.mainCamera.cullingMask & (1 << renderer.gameObject.layer)) == 0)
                    throw new InvalidOperationException("Other player's body is hidden; owner=" + owner + "; actor=" + id + "; layer=" + renderer.gameObject.layer + "; enabled=" + renderer.enabled);
            }
        }
        acknowledged.Add(clientPhase);
        NetworkClient.Send(new CoopAssimAck { phase = clientPhase, evidence = "owner=" + owner + "; all three types and rendered textures match; external bodies enabled and camera-visible" });
    }
    private void LateUpdate()
    {
        if (!Enabled || !NetworkServer.active || GameSystem.instance == null || finished) return;
        var game = GameSystem.instance;
        if (!started)
        {
            if (ready.Count < 2 || !NetworkManager.singleton.GetComponent<CoopWorld>().SharedWorldRunning) return;
            players = NetworkManager.singleton.GetComponent<CoopPlayerRegistry>().Players.OrderBy(p => p.CoopPlayerId).ToList();
            if (players.Count != 3) return;
            originalPause = GameSystem.settings.pauseOnTF; originalVictimPause = GameSystem.settings.pauseOnVictimTF;
            GameSystem.settings.pauseOnTF = GameSystem.settings.pauseOnVictimTF = false;
            testNode = game.map.rooms.Where(r => !r.locked).SelectMany(r => r.pathNodes).First(n => n.hasArea && n.area.width >= 8 && n.area.height >= 8);
            testLocation = testNode.RandomLocation(3f);
            started = true;
            players[1].UpdateToType(NPCType.GetDerivedType(Pixie.npcType), true, false);
            BeginPhase();
        }
        foreach (NPCScript npc in game.activeCharacters.OfType<NPCScript>()) npc.enabled = false;
        if (Time.unscaledTime - phaseAt > 60) { finished = true; throw new InvalidOperationException("Assimilation phase timed out: " + phase + "; victim=" + victim.npcType.name + "; state=" + victim.currentAI.currentState.GetType().Name); }
        if (victim.currentAI.currentState is PixieTransformState transforming && transforming.transformer == attacker) sawOriginalTransform = true;
        if (!victim.npcType.SameAncestor(Pixie.npcType)) return;
        if (!sawOriginalTransform) throw new InvalidOperationException("Victim changed without the expected original player assimilation state");
        if (command.types == null)
        {
            command.ids = players.Select(p => p.CoopPlayerId).ToArray();
            command.types = players.Select(p => p.npcType.name).ToArray();
            command.pictures = players.Select(Picture).ToArray();
            SendCommand();
            Debug.Log("[CoopAssim] Phase " + phase + " original assimilation completed; attacker=" + attacker.CoopPlayerId + "; victim=" + victim.CoopPlayerId + "; state=" + victim.currentAI.currentState.GetType().Name);
        }
        if (acknowledged.Count < 2) return;
        phase++;
        if (phase == 3)
        {
            finished = true; Completed = true;
            foreach (var connection in NetworkServer.connections.Values)
                if (connection != NetworkServer.localConnection && connection.isAuthenticated) connection.Send(new CoopAssimFinished());
            GameSystem.settings.pauseOnTF = originalPause; GameSystem.settings.pauseOnVictimTF = originalVictimPause;
            Debug.Log("[CoopAssim] PASS: remote monster -> host, remote monster -> other remote player, newly assimilated remote monster -> remote player; all peers verified external appearances");
            return;
        }
        BeginPhase();
    }
    private void BeginPhase()
    {
        attacker = phase < 2 ? players[1] : players[2];
        victim = phase == 0 ? players[0] : phase == 1 ? players[2] : players[1];
        victim.UpdateToType(NPCType.GetDerivedType(Human.npcType), true, false);
        foreach (PlayerScript player in players)
        {
            Vector3 position = testLocation + Vector3.forward * (player == attacker ? 0 : player == victim ? 2 : -2);
            player.ForceRigidBodyPosition(testNode, position);
        }
        victim.TakeWillDamage(10000);
        Vector3 direction = victim.GetMidBodyWorldPoint() - attacker.rotationTransformReference.position;
        Vector3 angles = Quaternion.LookRotation(direction).eulerAngles;
        command = new CoopAssimCommand { phase = phase, attacker = attacker.CoopPlayerId, victim = victim.CoopPlayerId,
            yaw = angles.y, pitch = angles.x > 180 ? angles.x - 360 : angles.x };
        sawOriginalTransform = false; acknowledged.Clear(); phaseAt = Time.unscaledTime;
        SendCommand();
        Debug.Log("[CoopAssim] Phase " + phase + " ready for actual client secondary input; attacker=" + attacker.CoopPlayerId + "; victim=" + victim.CoopPlayerId + "; canTarget=" + attacker.npcType.secondaryActions[0].canTarget(attacker, victim) + "; state=" + victim.currentAI.currentState.GetType().Name);
    }
    private void SendCommand()
    {
        foreach (var connection in NetworkServer.connections.Values)
            if (connection != NetworkServer.localConnection && connection.isAuthenticated) connection.Send(command);
    }
    private void ReleaseInput()
    {
        // Another validation may already own input when the completion packet arrives.
        var manager = NetworkManager.singleton;
        var flow = manager == null ? null : manager.GetComponent<CoopPlayerInputFlow>();
        if (flow != null && ReferenceEquals(flow.ValidationInputSource, movementSource)) flow.ValidationInputSource = null;
        if (CoopActions.Instance != null && ReferenceEquals(CoopActions.Instance.ValidationInputSource, actionSource))
            CoopActions.Instance.ValidationInputSource = null;
        movementSource = null; actionSource = null;
    }
    private void OnDisable() { ReleaseInput(); }
    private static string Picture(PlayerScript actor)
    {
        Texture texture = actor.mainSpriteRenderer.sharedMaterial.mainTexture;
        if (texture == null || texture.width <= 1 || !actor.mainSpriteRenderer.sharedMaterial.shader.isSupported)
            throw new InvalidOperationException("Invalid rendered player texture: " + actor.CoopPlayerId + "; type=" + actor.npcType.name);
        return CoopTopology.Digest(CoopTextureReference.Capture(texture));
    }
}
