using System;
using Mirror;
using UnityEngine;

// Opt-in WAN exercise through the existing client input hooks. NPCs and world
// simulation stay active; the server still decides movement and attack outcomes.
public sealed class CoopNetworkDrive : MonoBehaviour
{
    private CoopPlayerInputFlow input;
    private CoopActions actions;
    private int actionPhase = -1;
    private float nextReport, travel;
    private Vector3 previous;
    private bool bound;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-coopNetworkDrive") >= 0)
            new GameObject("WAN input exercise").AddComponent<CoopNetworkDrive>();
    }

    private void Update()
    {
        var game = GameSystem.instance;
        if (NetworkServer.active || !NetworkClient.isConnected || NetworkManager.singleton == null ||
            game == null || game.player == null || !game.gameInProgress) return;
        if (!bound)
        {
            input = NetworkManager.singleton.GetComponent<CoopPlayerInputFlow>();
            actions = NetworkManager.singleton.GetComponent<CoopActions>();
            float began = Time.unscaledTime;
            input.ValidationInputSource = () => {
                float phase = (Time.unscaledTime - began) % 8;
                return new PlayerMovementInput {
                    forward = phase < 2 ? .65f : phase >= 6 ? -.65f : 0,
                    strafe = phase >= 3 && phase < 5 ? .65f : 0,
                    run = phase >= 6, yaw = Time.unscaledTime * 45 % 360 };
            };
            actions.ValidationInputSource = () =>
            {
                int phase = (int)(Time.unscaledTime * 2);
                var sample = new CoopActionSample { quickItem = -1 };
                if (phase != actionPhase)
                {
                    if (phase % 4 == 0) sample.down = 1u << (int)HoPInput.PrimaryAction;
                    if (phase % 4 == 1) sample.up = 1u << (int)HoPInput.PrimaryAction;
                    actionPhase = phase;
                }
                return sample;
            };
            previous = game.player.transform.position;
            bound = true;
            Debug.Log("[CoopNetworkDrive] started; original NPC simulation enabled");
        }
        Vector3 position = game.player.transform.position;
        float step = Vector3.Distance(previous, position);
        if (step < 4) travel += step;
        previous = position;
        if (Time.unscaledTime >= nextReport)
        {
            nextReport = Time.unscaledTime + 5;
            Debug.Log("[CoopNetworkDrive] travel=" + travel.ToString("F2") + "; actionPhase=" + actionPhase);
        }
    }

    private void OnDestroy()
    {
        if (input != null) input.ValidationInputSource = null;
        if (actions != null) actions.ValidationInputSource = null;
    }
}
