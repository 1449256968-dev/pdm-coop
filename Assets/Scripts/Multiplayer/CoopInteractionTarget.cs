using Mirror;
using UnityEngine;

// Original NPC interaction remains the offline path. In a hosted world, an
// authenticated player's real CharacterStatus is also a possible action target.
public static class CoopInteractionTarget
{
    public static CharacterStatus Resolve(Collider collider, PlayerScript actor)
    {
        if (collider == null) return null;
        CharacterStatus target = collider.GetComponentInParent<NPCScript>();
        if (target == null && (NetworkServer.active || NetworkClient.active)) target = collider.GetComponentInParent<PlayerScript>();
        return target == actor ? null : target;
    }
    public static bool CanVolunteer(CharacterStatus target, CharacterStatus volunteer) =>
        target != null && volunteer != null && target != volunteer && target.gameObject.activeInHierarchy &&
        volunteer.gameObject.activeInHierarchy && volunteer.npcType != null && volunteer.currentAI != null && !volunteer.shouldRemove &&
        !target.shouldRemove && target.npcType != null && target.currentAI != null &&
        target.currentAI.currentState != null && !(target.currentAI.currentState is IncapacitatedState) &&
        ((target.currentAI.currentState.GeneralTargetInState() && !target.currentAI.currentState.immobilisedState) ||
            target.currentAI.currentState is StatueImmobileState || target.currentAI.currentState is GargoyleRestingState ||
            target.currentAI.currentState is GoldenStatueState || target.currentAI.currentState is MannequinImmobileState) &&
        target.npcType.VolunteerTo != null && target.npcType.CanVolunteerTo != null &&
        target.npcType.CanVolunteerTo(target, volunteer);

    public static bool TryVolunteer(CharacterStatus target, CharacterStatus volunteer)
    {
        // Confirmation may return after the target died, transformed, or disconnected.
        if (CoopReplication.IsReplica || !CanVolunteer(target, volunteer)) return false;
        target.npcType.VolunteerTo(target, volunteer);
        return true;
    }
    public static bool IsWorldGeometry(Collider collider, PlayerScript actor) =>
        collider.gameObject.layer == LayerMask.NameToLayer("Default") && Resolve(collider, actor) == null;
}
