using System;
using System.Linq;
using Mirror;
using UnityEngine;

// Diagnostic opt-in only: exercise an actual disconnect/rejoin and a second world
// in the same two player processes after the existing gameplay scenario completes.
public sealed class CoopLifecycleProbe : MonoBehaviour
{
    private float resultAt = -1, actionAt;
    private int stage;
    private string address;
    private ushort port;
    private bool authorityTested, rollbackChecked;
    private float authorityAt, originalMultiplier;
    private uint authorityPoll;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Environment.GetCommandLineArgs().Contains("-coopLifecycleValidate"))
            new GameObject("Coop Lifecycle Validation").AddComponent<CoopLifecycleProbe>();
    }
    private void Update()
    {
        var manager = NetworkManager.singleton as CoopNetworkManager;
        var game = GameSystem.instance;
        if (manager == null || game == null) return;
        if (!NetworkServer.active && NetworkClient.active && CoopReplication.Instance.LocalPlayerId != 0)
        {
            if (!authorityTested)
            {
                string original = CoopRoomSettings.Capture(GameSystem.settings);
                var ui = game.generalSettingsUI;
                float value = ui.playerStatsMultiplierSlider.value;
                ui.playerStatsMultiplierSlider.SetValueWithoutNotify(2);
                ui.UpdatePlayerStatsMultiplier();
                ui.playerStatsMultiplierSlider.SetValueWithoutNotify(value);
                game.mainMenuUI.StartNewGame(false);
                if (CoopRoomSettings.CanEdit || CoopRoomSettings.Capture(GameSystem.settings) != original)
                    throw new InvalidOperationException("Client room setting callback changed authoritative settings");
                Debug.Log("[CoopLifecycle] Client room settings callbacks rejected");
                originalMultiplier = GameSystem.settings.playerStatsMultiplier;
                GameSystem.settings.playerStatsMultiplier = originalMultiplier + 1;
                authorityPoll = manager.GetComponent<CoopRoomSettings>().ClientEnforcementCount;
                authorityAt = Time.unscaledTime; authorityTested = true;
            }
            else if (!rollbackChecked && manager.GetComponent<CoopRoomSettings>().ClientEnforcementCount > authorityPoll)
            {
                if (GameSystem.settings.playerStatsMultiplier != originalMultiplier)
                    throw new InvalidOperationException("Client room settings tamper rollback failed");
                rollbackChecked = true;
                Debug.Log("[CoopLifecycle] Client room settings tamper rolled back");
            }
        }
        if (resultAt < 0)
        {
            if (!game.postGame) return;
            resultAt = Time.unscaledTime;
            address = manager.networkAddress; port = manager.KcpTransport.port;
        }
        bool host = Environment.GetCommandLineArgs().Contains("-coopHost");
        if (host)
        {
            if (stage == 0 && Time.unscaledTime - resultAt > 30)
            {
                game.mainMenuUI.StartNewGame(false); stage = 1; actionAt = Time.unscaledTime;
                Debug.Log("[CoopLifecycle] Host started second original world");
            }
            if (stage == 1 && Time.unscaledTime - actionAt > 10)
            {
                Debug.Log("[CoopLifecycle] Host second world; generation=" + manager.GetComponent<CoopWorld>().Generation + "; players=" + manager.GetComponent<CoopPlayerRegistry>().Count);
                stage = 2;
            }
        }
        else
        {
            if (stage == 0 && Time.unscaledTime - resultAt > 4)
            {
                manager.StopClient(); stage = 1; actionAt = Time.unscaledTime;
                Debug.Log("[CoopLifecycle] Client disconnected in same process");
            }
            else if (stage == 1 && Time.unscaledTime - actionAt > 3)
            {
                manager.JoinAddress(address, port); stage = 2;
                Debug.Log("[CoopLifecycle] Client rejoining same host");
            }
            else if (stage == 2 && CoopReplication.Instance.LocalPlayerId != 0)
            {
                stage = 3;
                Debug.Log("[CoopLifecycle] Rejoin applied; owner=" + CoopReplication.Instance.LocalPlayerId + "; postGame=" + game.postGame);
            }
            else if (stage == 3 && manager.GetComponent<CoopWorld>().Generation >= 2 && !game.postGame && CoopReplication.Instance.LocalPlayerId != 0)
            {
                stage = 4;
                Debug.Log("[CoopLifecycle] Client second world applied; owner=" + CoopReplication.Instance.LocalPlayerId + "; hp=" + game.player.hp + "; generation=" + manager.GetComponent<CoopWorld>().Generation);
            }
        }
    }
}
