using System;
using System.Collections;
using System.Linq;
using Mirror;
using UnityEngine;

// Explicit opt-in coverage for mixed species, player status bars, observers and volunteering.
[DefaultExecutionOrder(17000)]
public sealed class CoopPlayerIntegrationProbe : MonoBehaviour
{
    private bool started, confirmed, volunteerSeen, identityValidated;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Environment.GetCommandLineArgs().Contains("-coopPlayerIntegrationValidate"))
            new GameObject("Player Integration Validation").AddComponent<CoopPlayerIntegrationProbe>();
    }
    private void LateUpdate()
    {
        var manager = NetworkManager.singleton;
        var game = GameSystem.instance;
        if (manager == null || game == null || !game.gameInProgress) return;
        if (!NetworkServer.active && !confirmed && game.multiOptionUI.gameObject.activeSelf &&
            game.multiOptionUI.requestText.text.StartsWith("Coop player volunteer test"))
        {
            confirmed = true;
            game.multiOptionUI.ClickButton(game.multiOptionUI.buttons[1]);
        }
        if (!NetworkServer.active && confirmed && identityValidated && CoopReplication.Instance.LocalPlayerId != 0 &&
            !volunteerSeen && game.player.npcType != null &&
            game.player.npcType.SameAncestor(Mannequin.npcType) && !(game.player.currentAI.currentState is MannequinTransformState))
        {
            volunteerSeen = true;
            Debug.Log("[CoopPlayerVolunteer] CLIENT COMPLETE; species=Mannequin");
        }
        if (started) return;
        int expected = int.Parse(CoopPlayerSelection.Argument("-coopPerfPlayers") ?? "2");
        if (manager.GetComponent<CoopSession>().Names.Count < expected) return;
        var registry = manager.GetComponent<CoopPlayerRegistry>();
        if (NetworkServer.active ? registry.Count < expected : CoopReplication.Instance.LocalPlayerId == 0) return;
        if (manager.GetComponent<CoopNameplates>().Count < expected - 2) return;
        started = true;
        StartCoroutine(Validate());
    }
    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException("Player integration: " + detail);
    }
    private IEnumerator Validate()
    {
        var manager = NetworkManager.singleton;
        var game = GameSystem.instance;
        CoopPlayerControl.Apply(0);
        if (NetworkServer.active)
        {
            var node = game.map.rooms.Where(r => !r.locked && !r.isWater).SelectMany(r => r.pathNodes)
                .First(n => n.hasArea && n.area.width >= 8 && n.area.height >= 8);
            game.player.ForceRigidBodyPosition(node, node.centrePoint);
            game.player.ForceHorizontalRotation(0); game.player.ForceVerticalRotation(0);
            int at = 0;
            foreach (var remote in manager.GetComponent<CoopPlayerRegistry>().Players.Where(p => p != game.player && p.gameObject.activeSelf))
                remote.ForceRigidBodyPosition(node, node.centrePoint + new Vector3((at++ - .5f) * 2, 0, 3));
        }
        yield return new WaitForSecondsRealtime(1);
        bool observer = Environment.GetCommandLineArgs().Contains("-coopObserver");
        string species = CoopPlayerSelection.Argument("-coopSpecies") ?? "Human";
        Require(observer || game.player.npcType.SameAncestor(CoopPlayerSelection.ResolveSpecies(species)), "own starting species=" + species + "; actual=" + game.player.npcType.name);
        identityValidated = true;
        Require(NetworkServer.active || game.playerInactive == observer, "owner observer state");
        var actors = NetworkServer.active ? manager.GetComponent<CoopPlayerRegistry>().Players :
            manager.GetComponent<CoopReplication>().VisibleReplicas<PlayerScript>().Cast<PlayerScript>();
        foreach (var actor in actors.Where(a => a != game.player && a.gameObject.activeInHierarchy))
            Require(manager.GetComponent<CoopNameplates>().StatusBarsMatch(actor), "HP/spirit bars for player=" + actor.CoopPlayerId);
        Require(CoopNameplates.StatusFraction(10, 0) == 0 && CoopNameplates.StatusFraction(-1, 20) == 0 &&
            CoopNameplates.StatusFraction(30, 20) == 1, "bar limits");
        Require(!CoopInteractionTarget.TryVolunteer(null, game.player) && !CoopInteractionTarget.TryVolunteer(game.player, game.player), "invalid/stale volunteer targets");
        if (observer)
        {
            var target = actors.First(a => a != game.player && a.gameObject.activeInHierarchy);
            var collider = target.GetComponentInChildren<Collider>();
            Require(CoopInteractionTarget.Resolve(collider, null) == target, "observer resolves PlayerScript");
            game.observer.InspectCharacter(target);
            yield return null;
            Require(game.observer.InspectedCharacter == target && manager.GetComponent<CoopObserverPlayerView>().IsOpen &&
                manager.GetComponent<CoopObserverPlayerView>().Target == target, "observer player inspection");
        }
        Debug.Log("[CoopPlayerIntegration] PASS; species=" + species + "; observer=" + observer + "; bars=True; host=" + NetworkServer.active);
        yield return new WaitForEndOfFrame();
        string output = CoopPlayerSelection.Argument("-coopIntegrationOutput");
        if (output != null)
        {
            string role = NetworkServer.active ? "host" : "client-" + CoopReplication.Instance.LocalPlayerId;
            string path = System.IO.Path.Combine(output, role + ".png");
            var screenshot = ScreenCapture.CaptureScreenshotAsTexture();
            try { System.IO.File.WriteAllBytes(path, screenshot.EncodeToPNG()); }
            finally { Destroy(screenshot); }
            Debug.Log("[CoopPlayerVisual] Saved; path=" + path);
        }
        if (NetworkServer.active && game.player.npcType.SameAncestor(Mannequin.npcType))
        {
            var volunteer = actors.FirstOrDefault(a => a != game.player && a.gameObject.activeInHierarchy && a.npcType.SameAncestor(Human.npcType));
            if (volunteer == null) yield break;
            var target = game.player;
            using (new CoopActorContext(volunteer))
            {
                Require(CoopInteractionTarget.CanVolunteer(target, volunteer), "player target accepts human volunteer");
                game.questionUI.ShowDisplay("Coop player volunteer test", () => {}, () => {
                    Require(CoopInteractionTarget.TryVolunteer(target, volunteer), "confirmed player-to-player volunteer");
                    Require(volunteer.currentAI.currentState is MannequinTransformState, "original volunteer state");
                    Debug.Log("[CoopPlayerVolunteer] PASS; target=PlayerScript; state=MannequinTransformState; dialogue=True");
                    StartCoroutine(WaitForVolunteer(volunteer));
                });
            }
        }
    }
    private IEnumerator WaitForVolunteer(PlayerScript volunteer)
    {
        float until = Time.unscaledTime + 60;
        while (volunteer != null && volunteer.currentAI.currentState is MannequinTransformState && Time.unscaledTime < until)
            yield return null;
        Require(volunteer != null && volunteer.npcType.SameAncestor(Mannequin.npcType) &&
            !(volunteer.currentAI.currentState is MannequinTransformState), "player volunteer transformation finished");
        Debug.Log("[CoopPlayerVolunteer] COMPLETE; species=Mannequin; host=True");
    }
}
