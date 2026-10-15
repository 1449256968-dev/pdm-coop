using Mirror;
using UnityEngine;

// Original NPC interaction remains the offline path. In a hosted world, an
// authenticated player's real CharacterStatus is also a possible action target.
public static class CoopInteractionTarget
{
    public static CharacterStatus Resolve(Collider collider, PlayerScript actor)
    {
        if (collider == null) return null;
        CharacterStatus target = collider.GetComponent<NPCScript>();
        if (target == null && NetworkServer.active) target = collider.GetComponentInParent<PlayerScript>();
        return target == actor ? null : target;
    }
    public static bool IsWorldGeometry(Collider collider, PlayerScript actor) =>
        collider.gameObject.layer == LayerMask.NameToLayer("Default") && Resolve(collider, actor) == null;
}
