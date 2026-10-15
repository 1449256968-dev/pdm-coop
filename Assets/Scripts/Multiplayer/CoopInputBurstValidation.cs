using System;
using System.Linq;
using Mirror;
using UnityEngine;

[DefaultExecutionOrder(12000)]
public sealed class CoopInputBurstValidation : MonoBehaviour
{
    private float began = -1;
    private int checkedSamples;
    private bool reported;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Environment.GetCommandLineArgs().Contains("-coopInputBurstValidate"))
            new GameObject("Coop Input Burst Validation").AddComponent<CoopInputBurstValidation>();
    }
    private void LateUpdate()
    {
        var manager = NetworkManager.singleton as CoopNetworkManager;
        if (manager == null || !manager.GetComponent<CoopWorld>().SharedWorldRunning || GameSystem.instance == null) return;
        var flow = manager.GetComponent<CoopPlayerInputFlow>();
        if (!NetworkServer.active)
        {
            if (CoopReplication.Instance.LocalPlayerId == 0) return;
            flow.ValidationInputSource = () => new PlayerMovementInput { forward = .5f, yaw = 90 };
            return;
        }
        foreach (PlayerScript remote in manager.GetComponent<CoopPlayerRegistry>().Players.Where(p => p.IsRemotePlayer))
        {
            if (began < 0) began = Time.unscaledTime;
            if (Time.unscaledTime - began < 3) continue;
            // Read() deliberately returns neutral while a dialogue/UI is open.
            // This fixture checks transport coalescing, not gameplay input gating.
            if (!flow.TryLatestSample(remote.CoopPlayerId, out PlayerMovementInput latest, out uint sequence)) continue;
            if (latest.forward != .5f || (sequence & 1) != 0)
                throw new InvalidOperationException("Same-frame movement batch retained an old neutral input");
            checkedSamples++;
        }
        if (!reported && checkedSamples >= 120)
        {
            reported = true;
            Debug.Log("[CoopInputBurstTest] PASS: actual unreliable two-message batches retain the latest input; samples=" + checkedSamples);
        }
    }
}
