using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;

// View-only objects live outside actor hierarchies and never enter state replication.
[DefaultExecutionOrder(15000)]
public sealed class CoopNameplates : MonoBehaviour
{
    private sealed class Plate { public PlayerScript actor; public GameObject root; public UnityEngine.UI.Text text; }
    private readonly Dictionary<int, Plate> plates = new Dictionary<int, Plate>();
    private Font font;
    private Material material;
    private float refreshAt;
    public int Count => plates.Count;

    private void LateUpdate()
    {
        var game = GameSystem.instance;
        if ((!NetworkServer.active && !NetworkClient.active) || game == null || !game.gameInProgress)
        { Clear(); return; }
        if (Time.unscaledTime >= refreshAt)
        {
            refreshAt = Time.unscaledTime + .25f;
            var actors = NetworkServer.active
                ? GetComponent<CoopPlayerRegistry>().Players.ToList()
                : GetComponent<CoopReplication>().VisibleReplicas<PlayerScript>().Cast<PlayerScript>().ToList();
            var names = GetComponent<CoopSession>().Names;
            var alive = new HashSet<int>();
            foreach (var actor in actors)
            {
                if (actor == game.player || !names.TryGetValue(actor.CoopPlayerId, out string name)) continue;
                alive.Add(actor.CoopPlayerId);
                if (!plates.TryGetValue(actor.CoopPlayerId, out Plate plate))
                {
                    plate = Create(); plates.Add(actor.CoopPlayerId, plate);
                }
                plate.actor = actor;
                if (plate.text.text != name) plate.text.text = name;
            }
            foreach (int id in plates.Keys.Where(id => !alive.Contains(id)).ToArray())
            { Destroy(plates[id].root); plates.Remove(id); }
        }
        var camera = game.mainCamera;
        foreach (Plate plate in plates.Values)
        {
            var actor = plate.actor;
            var sprite = actor == null ? null : actor.mainSpriteRenderer;
            bool visible = camera != null && actor != null && actor.gameObject.activeInHierarchy && sprite != null && sprite.enabled;
            if (visible)
            {
                Bounds bounds = sprite.bounds;
                Vector3 top = new Vector3(bounds.center.x, bounds.max.y + .18f, bounds.center.z);
                plate.root.transform.SetPositionAndRotation(top, camera.transform.rotation);
                visible = camera.WorldToViewportPoint(top).z > 0f && !Occluded(camera.transform.position, top);
            }
            plate.root.SetActive(visible);
        }
    }
    public static bool Occluded(Vector3 camera, Vector3 head) =>
        Physics.Linecast(camera, head, GameSystem.defaultMask, QueryTriggerInteraction.Ignore);

    private Plate Create()
    {
        if (font == null) font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Arial" }, 32);
        if (material == null) material = new Material(Resources.Load<Shader>("CoopNameplate"));
        var root = new GameObject("Coop Username", typeof(RectTransform), typeof(Canvas));
        root.transform.SetParent(transform, false);
        root.transform.localScale = Vector3.one * .008f;
        var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace;
        var rect = root.GetComponent<RectTransform>(); rect.sizeDelta = new Vector2(640, 48);
        rect.pivot = new Vector2(.5f, 0f);
        var child = new GameObject("Name", typeof(RectTransform), typeof(UnityEngine.UI.Text));
        child.transform.SetParent(root.transform, false);
        var childRect = child.GetComponent<RectTransform>();
        childRect.anchorMin = Vector2.zero; childRect.anchorMax = Vector2.one;
        childRect.offsetMin = childRect.offsetMax = Vector2.zero;
        var text = child.GetComponent<UnityEngine.UI.Text>();
        text.font = font; text.fontSize = 28; text.alignment = TextAnchor.MiddleCenter;
        text.color = new Color(1f, .94f, .65f); text.supportRichText = false;
        text.raycastTarget = false; text.material = material;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return new Plate { root = root, text = text };
    }
    private void Clear()
    {
        foreach (var plate in plates.Values) if (plate.root != null) Destroy(plate.root);
        plates.Clear();
    }
    private void OnDestroy() { Clear(); if (material != null) Destroy(material); if (font != null) Destroy(font); }
}
