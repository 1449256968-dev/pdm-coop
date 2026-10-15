using Mirror;
using UnityEngine;
using System.Collections.Generic;

// These meshes are view-model hands/items, not another player's world body.
public static class CoopFirstPersonVisibility
{
    private sealed class Original { public GameObject main, off; public bool mainActive, offActive; }
    private static readonly Dictionary<PlayerScript, Original> originals = new Dictionary<PlayerScript, Original>();
    public static void Refresh(PlayerScript player)
    {
        if (player == null || (!NetworkServer.active && !NetworkClient.active)) return;
        if (!player.IsRemotePlayer && !originals.ContainsKey(player))
        {
            GameObject main = Branch(player.equippedWeaponTransform, player.equippedWeaponMeshRenderer);
            GameObject off = Branch(player.equippedItemTransform, player.equippedItemMeshRenderer);
            originals[player] = new Original { main = main, off = off, mainActive = main != null && main.activeSelf, offActive = off != null && off.activeSelf };
        }
        bool visible = !player.IsRemotePlayer && !player.vanityCamera;
        Set(player.equippedWeaponTransform, player.equippedWeaponMeshRenderer, visible);
        Set(player.equippedItemTransform, player.equippedItemMeshRenderer, visible);
    }
    private static void Set(Transform root, Renderer renderer, bool visible)
    {
        GameObject branch = Branch(root, renderer);
        if (branch != null && branch.activeSelf != visible) branch.SetActive(visible);
    }
    private static GameObject Branch(Transform root, Renderer renderer) => root != null ? root.gameObject : renderer != null ? renderer.gameObject : null;
    public static void RestoreLocal()
    {
        foreach (Original original in originals.Values)
        {
            if (original.main != null) original.main.SetActive(original.mainActive);
            if (original.off != null) original.off.SetActive(original.offActive);
        }
        originals.Clear();
    }
}
