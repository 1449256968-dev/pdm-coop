using System;
using System.Linq;
using Mirror;
using UnityEngine;

// Opt-in natural-round soak: original autopilot controls all players. Does not
// spawn/kill/transform actors, disable NPCs, accelerate time, or call EndGame.
public sealed class CoopPlaythroughProbe : MonoBehaviour
{
    private float nextReport;
    private byte controls;
    private bool configured, finished;
    private string previousType;
    private int previousItems = -1, changes;
    private Vector3 previousPosition;
    private float travel;
    private int localId;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-coopPlaythrough") >= 0)
            new GameObject("Natural multiplayer playthrough monitor").AddComponent<CoopPlaythroughProbe>();
    }

    private void Update()
    {
        var manager = NetworkManager.singleton;
        var game = GameSystem.instance;
        if (finished || manager == null || game == null || game.player == null ||
            CoopReplication.Instance == null || !NetworkClient.isConnected) return;
        int id = NetworkServer.active ? game.player.CoopPlayerId : CoopReplication.Instance.LocalPlayerId;
        if (id == 0) return;
        if (!configured)
        {
            controls = CoopPlayerControl.Capture();
            CoopPlayerControl.Apply(15);
            localId = id;
            previousPosition = game.player.transform.position;
            configured = true;
            Debug.Log("[CoopPlaythrough] START; player=" + id + "; host=" + NetworkServer.active + "; originalAutopilot=True");
        }
        if (id != localId) throw new InvalidOperationException("Playthrough local owner changed without reconnect");
        string type = game.player.npcType == null ? "null" : game.player.npcType.name;
        int items = game.player.currentItems.Count;
        if (previousType != null && type != previousType) changes++;
        if (type != previousType || items != previousItems)
            Debug.Log("[CoopPlaythrough] EVENT; player=" + id + "; type=" + type + "; hp=" + game.player.hp + "; items=" + items);
        previousType = type; previousItems = items;
        Vector3 position = game.player.transform.position;
        float distance = Vector3.Distance(previousPosition, position);
        if (distance < 4) travel += distance;
        previousPosition = position;
        if (Time.unscaledTime >= nextReport)
        {
            nextReport = Time.unscaledTime + 5;
            Debug.Log("[CoopPlaythrough] STATE; player=" + id + "; host=" + NetworkServer.active +
                "; gameTime=" + game.totalGameTime.ToString("F1") + "; hp=" + game.player.hp +
                "; type=" + type + "; items=" + items + "; typeChanges=" + changes +
                "; travel=" + travel.ToString("F2") + "; active=" + game.player.gameObject.activeSelf +
                "; paused=" + game.pauseAllInteraction + "/" + game.pauseActionOnly +
                "; results=" + game.resultsDisplay.activeSelf + "; postGame=" + game.postGame);
            if (NetworkServer.active)
            {
                var players = manager.GetComponent<CoopPlayerRegistry>().Players.ToArray();
                if (players.Select(p => p.CoopPlayerId).Distinct().Count() != players.Length)
                    throw new InvalidOperationException("Playthrough duplicate player ownership");
                foreach (var player in players)
                    Debug.Log("[CoopPlaythrough] AUTHORITY; player=" + player.CoopPlayerId + "; hp=" + player.hp +
                        "; type=" + player.npcType.name + "; items=" + player.currentItems.Count +
                        "; position=" + player.transform.position);
            }
        }
        if (game.postGame && !game.gameInProgress)
        {
            if (!game.resultsDisplay.activeSelf || string.IsNullOrEmpty(game.resultsText.text))
                throw new InvalidOperationException("Playthrough ended without visible result text");
            Debug.Log("[CoopPlaythrough] COMPLETE; player=" + id + "; typeChanges=" + changes +
                "; travel=" + travel.ToString("F2") + "; result=" + game.resultsText.text.Replace('\n', '|'));
            var args = Environment.GetCommandLineArgs();
            int logIndex = Array.IndexOf(args, "-logFile");
            if (logIndex >= 0 && logIndex + 1 < args.Length)
                ScreenCapture.CaptureScreenshot(args[logIndex + 1] + ".result.png");
            finished = true;
        }
    }

    private void OnDestroy()
    {
        if (configured && GameSystem.settings != null) CoopPlayerControl.Apply(controls);
    }
}
