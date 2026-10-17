using System.Collections.Generic;
using Mirror;
using UnityEngine;

public struct CoopAttackArcMessage : NetworkMessage
{
    public uint world;
    public Vector3 feet;
    public float yaw, halfArc, range;
}

// Short-lived effects must not depend on the next full-world snapshot. Only
// the host emits this event after the original attack has actually executed.
public sealed class CoopAttackEffects : MonoBehaviour
{
    private struct Effect { public AttackArc arc; public float began; public uint world; }
    private readonly List<Effect> effects = new List<Effect>();
    public long ArcsShown { get; private set; }
    public void StartClient() { Clear(); NetworkClient.RegisterHandler<CoopAttackArcMessage>(Receive); }
    public void StopClient() { NetworkClient.UnregisterHandler<CoopAttackArcMessage>(); Clear(); }
    public static void Publish(Vector3 feet, float yaw, float halfArc, float range)
    {
        if (!NetworkServer.active || NetworkManager.singleton == null) return;
        var world = NetworkManager.singleton.GetComponent<CoopWorld>();
        if (world == null || !world.SharedWorldRunning) return;
        var message = new CoopAttackArcMessage { world = world.Generation, feet = feet, yaw = yaw, halfArc = halfArc, range = range };
        foreach (var connection in NetworkServer.connections.Values)
            if (connection != null && connection.isAuthenticated && connection.isReady && connection != NetworkServer.localConnection)
                connection.Send(message);
    }
    private void Receive(CoopAttackArcMessage message)
    {
        var world = GetComponent<CoopWorld>();
        if (NetworkServer.active || !CoopReplication.IsReplica || !world.SharedWorldRunning || message.world != world.Generation) return;
        var prefab = (GameObject)GameSystem.instance.Prefabs[typeof(AttackArc)];
        var instance = Instantiate(prefab);
        // Explicit local lifetime: no pooled world identity, simulation, or hit logic.
        foreach (var behaviour in instance.GetComponentsInChildren<MonoBehaviour>(true)) behaviour.enabled = false;
        instance.SetActive(true);
        var arc = instance.GetComponent<AttackArc>();
        arc.Initialise(message.feet, message.yaw, message.halfArc, message.range);
        arc.CoopUpdatePresentation(0);
        effects.Add(new Effect { arc = arc, began = Time.unscaledTime, world = message.world });
        ArcsShown++;
    }
    private void LateUpdate()
    {
        var world = GetComponent<CoopWorld>();
        for (int i = effects.Count - 1; i >= 0; i--)
        {
            var effect = effects[i];
            float age = Time.unscaledTime - effect.began;
            if (effect.arc == null || !world.SharedWorldRunning || effect.world != world.Generation || age > .25f)
            {
                if (effect.arc != null) Destroy(effect.arc.gameObject);
                effects.RemoveAt(i);
            }
            else effect.arc.CoopUpdatePresentation(age);
        }
    }
    private void Clear()
    {
        foreach (var effect in effects) if (effect.arc != null) Destroy(effect.arc.gameObject);
        effects.Clear(); ArcsShown = 0;
    }
    private void OnDestroy() { Clear(); }
}
