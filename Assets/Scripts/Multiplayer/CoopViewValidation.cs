using System;
using System.Linq;
using System.Collections;
using System.IO;
using Mirror;
using UnityEngine;

[DefaultExecutionOrder(11000)]
public sealed class CoopViewValidation : MonoBehaviour
{
    private float nextCheck, began = -1;
    private bool reported;
    private void Start() { CoopMotion.ValidateInterpolation(); }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Environment.GetCommandLineArgs().Contains("-coopViewValidate"))
            new GameObject("Coop View Validation").AddComponent<CoopViewValidation>();
    }
    private static void Check(PlayerScript player, bool expected)
    {
        foreach (Transform hand in new[] { player.equippedWeaponTransform, player.equippedItemTransform })
            if (hand != null && hand.gameObject.activeSelf != expected)
                throw new InvalidOperationException("First-person hand ownership failed: player=" + player.CoopPlayerId + "; expectedVisible=" + expected);
    }
    private void LateUpdate()
    {
        var game = GameSystem.instance;
        if (game == null || !game.gameInProgress || CoopReplication.Instance == null ||
            (!NetworkServer.active && CoopReplication.Instance.LocalPlayerId == 0) || Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + .5f;
        var remotes = NetworkServer.active ? NetworkManager.singleton.GetComponent<CoopPlayerRegistry>().Players.Where(p => p.IsRemotePlayer).ToList()
            : CoopReplication.Instance.VisibleReplicas<PlayerScript>().Cast<PlayerScript>().Where(p => p.IsRemotePlayer).ToList();
        if (remotes.Count == 0) return;
        foreach (PlayerScript remote in remotes)
        {
            Check(remote, false);
            if (remote.gameObject.activeInHierarchy && (!remote.mainSpriteRenderer.enabled || !remote.mainSpriteRenderer.gameObject.activeInHierarchy))
                throw new InvalidOperationException("Hand suppression hid the remote world body");
        }
        bool vanity = game.player.vanityCamera;
        Check(game.player, !vanity);
        game.player.vanityCamera = true; CoopFirstPersonVisibility.Refresh(game.player); Check(game.player, false);
        game.player.vanityCamera = false; CoopFirstPersonVisibility.Refresh(game.player); Check(game.player, true);
        game.player.vanityCamera = vanity; CoopFirstPersonVisibility.Refresh(game.player);
        if (began < 0) began = Time.unscaledTime;
        if (!reported && Time.unscaledTime - began >= 8)
        {
            reported = true;
            Debug.Log("[CoopViewTest] PASS: remote first-person hands hidden, remote world bodies active, local first-person hands retained, vanity view hides both hands");
            StartCoroutine(CaptureFrame());
        }
    }
    private IEnumerator CaptureFrame()
    {
        yield return new WaitForEndOfFrame();
        Texture2D frame = ScreenCapture.CaptureScreenshotAsTexture();
        string role = NetworkServer.active ? "host" : "client-" + CoopReplication.Instance.LocalPlayerId;
        string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "CoopView-" + role + ".png");
        try { File.WriteAllBytes(path, frame.EncodeToPNG()); Debug.Log("[CoopViewTest] Rendered frame saved: " + path); }
        finally { Destroy(frame); }
    }
}
