using System;
using System.Linq;
using Mirror;
using UnityEngine;

// Opt-in real-peer checks, including a real GPU render of the nameplate shader.
public sealed class CoopIdentityValidation : MonoBehaviour
{
    private int phase;
    private float began;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Environment.GetCommandLineArgs().Contains("-coopIdentityValidate"))
            new GameObject("Coop Identity Validation").AddComponent<CoopIdentityValidation>();
    }
    private void Update()
    {
        var game = GameSystem.instance;
        var manager = NetworkManager.singleton;
        if (game == null || !game.gameInProgress || manager == null || phase == 2) return;
        var names = manager.GetComponent<CoopSession>().Names;
        if (names.Count != 3 || manager.GetComponent<CoopNameplates>().Count != 2) return;
        if (phase == 0)
        {
            Require(names.Values.Contains("房主Alpha") && names.Values.Contains("玩家Beta") && names.Values.Contains("玩家Gamma"), "all usernames replicated");
            Require(CoopUsername.Clean(" <b>玩家\n</b> ").IndexOf('<') < 0 && CoopUsername.Clean(new string('x', 50)).Length == 24, "name boundaries");
            game.SwapToAndFromMainGameUI(false); game.mainMenuUI.ShowDisplay();
            began = Time.unscaledTime; phase = 1; return;
        }
        if (Time.unscaledTime - began < 1f) return;
        phase = 2; // A failed assertion must report once, not every frame.
        var menu = game.mainMenuUI;
        var buttons = menu.GetComponentsInChildren<UnityEngine.UI.Button>(true);
        var methods = new[] { "ShowSettings", "ShowSpawnSettings", "ShowItemSettings", "ShowDiplomacySettings" };
        foreach (string method in methods)
        {
            var button = buttons.First(b => Enumerable.Range(0, b.onClick.GetPersistentEventCount()).Any(i => b.onClick.GetPersistentMethodName(i) == method));
            Require(button.interactable == NetworkServer.active, "settings entry authority: " + method);
            if (!NetworkServer.active) typeof(MainMenuUI).GetMethod(method).Invoke(menu, null);
        }
        if (!NetworkServer.active)
        {
            game.settingsUI.ShowDisplay(); game.spawnSettingsUI.ShowDisplay();
            game.itemSettingsUI.ShowDisplay(); game.diplomacySettingsUI.ShowDisplay();
            Require(menu.gameObject.activeSelf && !game.settingsUI.gameObject.activeSelf && !game.generalSettingsUI.gameObject.activeSelf &&
                !game.spawnSettingsUI.gameObject.activeSelf && !game.itemSettingsUI.gameObject.activeSelf && !game.diplomacySettingsUI.gameObject.activeSelf, "client settings menus remain closed");
        }
        else
        {
            menu.ShowSettings(); Require(game.settingsUI.gameObject.activeSelf, "host settings open");
            game.settingsUI.gameObject.SetActive(false);
            game.inputWatcher.inputWatchers.Remove(game.settingsUI);
        }
        menu.gameObject.SetActive(false); game.SwapToAndFromMainGameUI(true);
        CheckOcclusion(manager.GetComponent<CoopNameplates>());
        Debug.Log("[CoopIdentity] PASS; names=3; plates=2; menuAuthority=True; wallOcclusion=True; host=" + NetworkServer.active);
        StartCoroutine(CheckMenuReturn());
        phase = 2;
    }
    private static void Require(bool condition, string test)
    { if (!condition) throw new InvalidOperationException("Coop identity validation failed: " + test); }
    private System.Collections.IEnumerator CheckMenuReturn()
    {
        var game = GameSystem.instance;
        var manager = NetworkManager.singleton;
        byte original = CoopPlayerControl.Capture();
        if (NetworkServer.active)
        {
            var node = game.map.rooms.Where(r => !r.locked && !r.isWater).SelectMany(r => r.pathNodes)
                .First(n => n.hasArea && n.area.width >= 10 && n.area.height >= 10);
            int index = 0;
            foreach (var actor in manager.GetComponent<CoopPlayerRegistry>().Players.Where(p => p.IsRemotePlayer))
                actor.ForceRigidBodyPosition(node, node.centrePoint + Vector3.forward * (++index == 1 ? -2f : 2f));
            float deadline = Time.unscaledTime + 12f;
            while (Time.unscaledTime < deadline)
            {
                Require(CoopPlayerControl.Capture() == original, "client AI options must not change host preference");
                yield return null;
            }
            Debug.Log("[CoopMenu] HOST PASS: personal AI preference isolation");
            yield break;
        }
        var menu = game.mainMenuUI;
        var settings = game.generalSettingsUI;
        string room = CoopRoomSettings.Capture(GameSystem.settings);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            yield return null;
            CoopActions.PauseMenuEscape();
            Require(menu.gameObject.activeSelf && game.uisBeforeMainUI == 1, "one pause menu layer");
            CoopActions.PauseMenuEscape();
            Require(game.uisBeforeMainUI == 1, "duplicate same-frame Escape ignored");
            if (cycle == 1)
            {
                yield return null;
                CoopActions.PauseMenuEscape();
            }
            else
            {
                menu.ShowGeneralSettings(); settings.ShowDisplay();
                yield return new WaitForSecondsRealtime(.4f);
                Require(settings.gameObject.activeSelf && game.inputWatcher.inputWatchers.Count(w => w == settings) == 1, "general settings owns input once");
                Require(settings.autopilotHumanToggle.interactable && settings.autopilotActiveToggle.interactable && settings.autopilotPassiveToggle.interactable,
                    "client AI controls remain editable");
                Require(!settings.playerStatsMultiplierSlider.interactable, "room control remains read-only");
                settings.autopilotHumanToggle.isOn = !settings.autopilotHumanToggle.isOn;
                settings.autopilotActiveToggle.isOn = !settings.autopilotActiveToggle.isOn;
                settings.autopilotPassiveToggle.isOn = !settings.autopilotPassiveToggle.isOn;
                Require(GameSystem.settings.autopilotHuman == settings.autopilotHumanToggle.isOn &&
                    GameSystem.settings.autopilotActive == settings.autopilotActiveToggle.isOn &&
                    GameSystem.settings.autopilotPassive == settings.autopilotPassiveToggle.isOn, "AI checkbox callbacks applied");
                Require(CoopRoomSettings.Capture(GameSystem.settings) == room, "AI choices preserve room rules");
                settings.Back();
                CoopActions.PauseMenuEscape();
                Require(menu.gameObject.activeSelf, "sub-menu Back consumes Escape for this frame");
                if (cycle == 2)
                {
                    // Reproduce/repair the stale count and orphaned UI owner from
                    // builds where Escape opened an already-open menu repeatedly.
                    game.uisBeforeMainUI = 3;
                    game.inputWatcher.inputWatchers.Add(settings);
                }
                menu.ResumeGame();
            }
            Require(game.mainGameUI.activeSelf && !game.pauseAllInteraction && game.uisBeforeMainUI == 0 &&
                game.inputWatcher.inputWatchers.Count == 0 && !menu.gameObject.activeSelf, "HUD and input restored after menu return");
        }
        CoopPlayerControl.Apply(0);
        game.player.horizontalPlaneRotation = 90; game.player.CoopViewPitch = 13;
        var flow = manager.GetComponent<CoopPlayerInputFlow>();
        Vector3 position = game.player.transform.position;
        flow.ValidationInputSource = () => new PlayerMovementInput { forward = 1, yaw = 90 };
        yield return new WaitForSecondsRealtime(2.5f);
        flow.ValidationInputSource = null;
        float moved = Vector3.ProjectOnPlane(game.player.transform.position - position, Vector3.up).magnitude;
        Require(moved > .1f && game.mainGameUI.activeSelf && Mathf.Abs(game.player.CoopViewPitch - 13) < 1f, "movement and look after Resume; moved=" + moved);
        CoopPlayerControl.Apply(original);
        Debug.Log("[CoopMenu] CLIENT PASS: three menu cycles; AI choices editable; room rules locked; HUD/input restored; moved=" + moved);
    }
    private static void CheckOcclusion(CoopNameplates source)
    {
        var original = source.GetComponentInChildren<UnityEngine.UI.Text>(true);
        var origin = new Vector3(10000, 10000, 10000);
        var cameraObject = new GameObject("Nameplate test camera", typeof(Camera));
        var camera = cameraObject.GetComponent<Camera>();
        camera.transform.position = origin; camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black; camera.cullingMask = 1 << 31;
        camera.orthographic = true; camera.orthographicSize = 1; camera.nearClipPlane = .1f; camera.farClipPlane = 10;
        var texture = new RenderTexture(256, 256, 24); camera.targetTexture = texture;
        // Offscreen/occluded plates are inactive; Graphic.canvas can be null then.
        var label = Instantiate(original.transform.parent.gameObject);
        foreach (Transform child in label.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 31;
        label.SetActive(true); label.transform.position = origin + Vector3.forward * 5;
        label.transform.rotation = Quaternion.identity;
        label.GetComponent<Canvas>().worldCamera = camera;
        var text = label.GetComponentInChildren<UnityEngine.UI.Text>(); text.text = "Username";
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.layer = 0; wall.transform.position = origin + Vector3.forward * 3;
        wall.transform.localScale = new Vector3(10, 10, .2f);
        Physics.SyncTransforms();
        Require(CoopNameplates.Occluded(origin, origin + Vector3.forward * 5), "wall linecast blocks");
        wall.SetActive(false); Physics.SyncTransforms();
        Require(!CoopNameplates.Occluded(origin, origin + Vector3.forward * 5), "clear linecast visible");
        Canvas.ForceUpdateCanvases(); camera.Render(); int clear = TextPixels(texture);
        wall.layer = 31; wall.SetActive(true); Canvas.ForceUpdateCanvases(); camera.Render(); int blocked = TextPixels(texture);
        Require(clear > 10 && blocked == 0, "GPU depth occlusion; clear=" + clear + "; blocked=" + blocked);
        Debug.Log("[CoopIdentity] GPU nameplate pixels; clear=" + clear + "; behindWall=" + blocked);
        camera.targetTexture = null; texture.Release(); Destroy(texture); Destroy(label); Destroy(wall); Destroy(cameraObject);
    }
    private static int TextPixels(RenderTexture target)
    {
        var prior = RenderTexture.active; RenderTexture.active = target;
        var image = new Texture2D(256, 256, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); image.Apply();
        int count = image.GetPixels32().Count(c => c.r > 180 && c.g > 170 && c.b < 200);
        RenderTexture.active = prior; Destroy(image); return count;
    }
}
