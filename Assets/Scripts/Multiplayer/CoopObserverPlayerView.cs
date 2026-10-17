using Mirror;
using UnityEngine;

// Read-only shared CharacterStatus inspection; never runs the NPC sandbox's possess/delete paths.
public sealed class CoopObserverPlayerView : MonoBehaviour
{
    public CharacterStatus Target { get; private set; }
    public bool IsOpen => surface != null && surface.activeSelf;
    public bool BlocksInput => IsOpen || closedFrame == Time.frameCount;
    private int closedFrame = -1;
    private GameObject surface;
    private UnityEngine.UI.Text details;
    private bool cursorVisible;
    private CursorLockMode cursorLock;
    private float nextRefresh;

    public void Show(CharacterStatus target)
    {
        if (target == null) return;
        if (surface == null) Create();
        if (!IsOpen) { cursorVisible = Cursor.visible; cursorLock = Cursor.lockState; }
        Target = target;
        surface.SetActive(true); Cursor.visible = true; Cursor.lockState = CursorLockMode.None;
        nextRefresh = 0;
    }
    public void Hide()
    {
        if (!IsOpen) return;
        surface.SetActive(false); Target = null;
        closedFrame = Time.frameCount;
        Cursor.visible = cursorVisible; Cursor.lockState = cursorLock;
    }
    private void Update()
    {
        if (!IsOpen) return;
        var game = GameSystem.instance;
        if (Target == null || Target.npcType == null || game == null || !game.gameInProgress ||
            (!NetworkServer.active && !NetworkClient.active) || Input.GetKeyDown(KeyCode.Escape))
        { Hide(); return; }
        if (Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + .1f;
        string name = Target.characterName;
        if (Target is PlayerScript player && GetComponent<CoopSession>().Names.TryGetValue(player.CoopPlayerId, out string username)) name = username;
        details.text = name + "\n种族：" + Target.npcType.name +
            "\n生命：" + Target.hp + " / " + Target.npcType.hp +
            "\n精神：" + Target.will + " / " + Target.npcType.will +
            "\n耐力：" + Target.stamina.ToString("F0") + " / " + Target.npcType.stamina.ToString("F0");
    }
    private void Create()
    {
        surface = new GameObject("Coop Observer Inspection", typeof(RectTransform), typeof(Canvas),
            typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
        surface.transform.SetParent(transform, false);
        var canvas = surface.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 30000;
        var scaler = surface.GetComponent<UnityEngine.UI.CanvasScaler>();
        scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(960, 640);
        var panel = new GameObject("Character Info", typeof(RectTransform), typeof(UnityEngine.UI.Image));
        panel.transform.SetParent(surface.transform, false);
        var rect = panel.GetComponent<RectTransform>(); rect.sizeDelta = new Vector2(400, 260);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f); rect.anchoredPosition = Vector2.zero;
        panel.GetComponent<UnityEngine.UI.Image>().color = new Color(.04f, .06f, .09f, .98f);
        details = new GameObject("Details", typeof(RectTransform), typeof(UnityEngine.UI.Text)).GetComponent<UnityEngine.UI.Text>();
        details.transform.SetParent(rect, false);
        var content = details.rectTransform; content.anchorMin = new Vector2(0, .22f); content.anchorMax = Vector2.one;
        content.offsetMin = new Vector2(20, 0); content.offsetMax = new Vector2(-20, -16);
        details.font = Resources.Load<Font>("Coop/NotoSansCJKsc-Regular"); details.fontSize = 23; details.color = Color.white; details.raycastTarget = false;
        details.supportRichText = false;
        var button = new GameObject("Close", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
        button.transform.SetParent(rect, false);
        var buttonRect = button.GetComponent<RectTransform>(); buttonRect.anchorMin = buttonRect.anchorMax = new Vector2(.5f, 0);
        buttonRect.anchoredPosition = new Vector2(0, 28); buttonRect.sizeDelta = new Vector2(160, 36);
        button.GetComponent<UnityEngine.UI.Image>().color = new Color(.12f, .27f, .36f);
        button.GetComponent<UnityEngine.UI.Button>().targetGraphic = button.GetComponent<UnityEngine.UI.Image>();
        button.GetComponent<UnityEngine.UI.Button>().onClick.AddListener(Hide);
        var label = new GameObject("Label", typeof(RectTransform), typeof(UnityEngine.UI.Text)).GetComponent<UnityEngine.UI.Text>();
        label.transform.SetParent(buttonRect, false); label.rectTransform.anchorMin = Vector2.zero; label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;
        label.font = details.font; label.fontSize = 21; label.text = "关闭 / Esc"; label.alignment = TextAnchor.MiddleCenter; label.color = Color.white; label.raycastTarget = false;
        surface.SetActive(false);
    }
    private void OnDisable() { Hide(); }
}
