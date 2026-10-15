using System.Collections.Generic;
using Mirror;
using UnityEngine;

// These orientations belong to the viewer, not the authoritative simulation.
// Run after pose interpolation, before nameplates read the final sprite bounds.
[DefaultExecutionOrder(13000)]
public sealed class CoopBillboards : MonoBehaviour
{
    private void LateUpdate()
    {
        var game = GameSystem.instance;
        if (game == null || !game.gameInProgress || game.activePrimaryCameraTransform == null ||
            (!NetworkServer.active && !CoopReplication.IsReplica)) return;
        FaceView(game.activePrimaryCameraTransform.position);
    }
    public void FaceView(Vector3 eye)
    {
        IEnumerable<Component> entities = NetworkServer.active
            ? GetComponent<CoopPlayerRegistry>().Players
            : GetComponent<CoopReplication>().ReplicaEntities;
        foreach (Component component in entities)
        {
            if (component == null || !component.gameObject.activeInHierarchy) continue;
            Vector3 forward = component.transform.position - eye; forward.y = 0f;
            if (forward.sqrMagnitude < .000001f) continue;
            Quaternion rotation = Quaternion.LookRotation(forward);
            if (component is PlayerScript player)
            {
                // Keep the remote actor's aim and physics pose authoritative. Only
                // the flat world-body quad needs to face this viewer.
                if (player.IsRemotePlayer)
                    player.playerGameSpriteTransform.rotation = rotation * Quaternion.Euler(player.extraSpriteXRotation, 0, 0);
            }
            else if (component is NPCScript npc)
            {
                npc.mainSpriteTransformReference.localRotation = Quaternion.Euler(npc.extraSpriteXRotation, 0, 0);
                if (!npc.facingLocked) npc.directTransformReference.rotation = rotation;
                npc.statusSectionTransformReference.rotation = rotation;
            }
            else if (component is ItemOrb orb) orb.directTransformReference.rotation = rotation;
            else if (component is Portal portal) portal.directTransformReference.rotation = rotation;
        }
    }
}
