using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;

// View-only objects live outside actor hierarchies and never enter state replication.
[DefaultExecutionOrder(15000)]
public sealed class CoopNameplates : MonoBehaviour
{
    private sealed class Plate { public PlayerScript actor; public GameObject root, health, focus; public RectTransform hpFill, willFill; public UnityEngine.UI.Text text; }
    private readonly Dictionary<int, Plate> plates = new Dictionary<int, Plate>();
    private Font font;
    private Material material;
    private float refreshAt;
    public int Count => plates.Count;
    public bool StatusBarsMatch(PlayerScript actor)
    {
        if (actor == null || actor.npcType == null || !plates.TryGetValue(actor.CoopPlayerId, out var plate)) return false;
        return Mathf.Abs(plate.hpFill.localScale.x - StatusFraction(actor.hp, actor.npcType.hp)) < .001f &&
            Mathf.Abs(plate.willFill.localScale.x - StatusFraction(actor.will, actor.npcType.will)) < .001f &&
            plate.health.activeSelf == (GameSystem.settings.showStatusBars && actor.npcType.hp > 0) &&
            plate.focus.activeSelf == (GameSystem.settings.showStatusBars && actor.npcType.will > 0);
    }

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
        var camera = game.playerInactive ? game.observer.GetComponent<Camera>() : game.mainCamera;
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
            plate.health.SetActive(GameSystem.settings.showStatusBars && actor != null && actor.npcType != null && actor.npcType.hp > 0);
            plate.focus.SetActive(GameSystem.settings.showStatusBars && actor != null && actor.npcType != null && actor.npcType.will > 0);
            if (actor != null && actor.npcType != null)
            {
                plate.hpFill.localScale = new Vector3(StatusFraction(actor.hp, actor.npcType.hp), 1, 1);
                plate.willFill.localScale = new Vector3(StatusFraction(actor.will, actor.npcType.will), 1, 1);
            }
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
        childRect.offsetMin = new Vector2(0, 16); childRect.offsetMax = Vector2.zero;
        var text = child.GetComponent<UnityEngine.UI.Text>();
        text.font = font; text.fontSize = 28; text.alignment = TextAnchor.MiddleCenter;
        text.color = new Color(1f, .94f, .65f); text.supportRichText = false;
        text.raycastTarget = false; text.material = material;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        var plate = new Plate { root = root, text = text };
        plate.health = Bar(root.transform, "Health", 8, new Color(.9f, .12f, .12f), out plate.hpFill);
        plate.focus = Bar(root.transform, "Spirit", 0, new Color(.22f, .55f, 1f), out plate.willFill);
        return plate;
    }
    public static float StatusFraction(float value, float maximum) => maximum > 0 ? Mathf.Clamp01(value / maximum) : 0;
    private GameObject Bar(Transform parent, string name, float y, Color color, out RectTransform fill)
    {
        var root = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Image));
        root.transform.SetParent(parent, false);
        var rect = root.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(.5f, 0); rect.pivot = new Vector2(.5f, 0);
        rect.anchoredPosition = new Vector2(0, y); rect.sizeDelta = new Vector2(100, 6);
        var backing = root.GetComponent<UnityEngine.UI.Image>(); backing.color = new Color(.04f, .04f, .04f, .9f);
        backing.raycastTarget = false; backing.material = material;
        fill = new GameObject("Fill", typeof(RectTransform), typeof(UnityEngine.UI.Image)).GetComponent<RectTransform>();
        fill.SetParent(rect, false); fill.anchorMin = fill.anchorMax = fill.pivot = Vector2.zero;
        fill.sizeDelta = rect.sizeDelta;
        var image = fill.GetComponent<UnityEngine.UI.Image>(); image.color = color; image.raycastTarget = false; image.material = material;
        return root;
    }
    private void Clear()
    {
        foreach (var plate in plates.Values) if (plate.root != null) Destroy(plate.root);
        plates.Clear();
    }
    private void OnDestroy() { Clear(); if (material != null) Destroy(material); if (font != null) Destroy(font); }
}
