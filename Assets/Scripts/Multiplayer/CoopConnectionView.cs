using System;
using Mirror;
using TMPro;
using UnityEngine;

/// <summary>Overlay Canvas keeps the connection form above the recovered game's UI.</summary>
public sealed class CoopConnectionView : MonoBehaviour
{
    private GameObject surface, blocker, joinPage, hostPage;
    private TMP_FontAsset font;
    private Font sourceFont;
    private TMP_InputField nameField, addressField, remotePortField, localPortField;
    private TMP_Text status, error, stopLabel;
    private UnityEngine.UI.Button joinTab, hostTab, connect, create, stop;
    private CoopConnectionPanel panel;
    private UnityEngine.UI.Button observerChoice;

    public void Initialize(CoopConnectionPanel owner)
    {
        panel = owner;
        var root = new GameObject("Connection Canvas", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
        root.transform.SetParent(transform, false);
        var canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 32760;
        var scaler = root.GetComponent<UnityEngine.UI.CanvasScaler>();
        scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(640, 720); scaler.matchWidthOrHeight = 1;
        sourceFont = Resources.Load<Font>("Coop/NotoSansCJKsc-Regular");
        if (sourceFont == null) throw new InvalidOperationException("Connection page CJK font is missing");
        font = TMP_FontAsset.CreateFontAsset(sourceFont, 48, 5, UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024);
        if (font == null) throw new InvalidOperationException("Connection page CJK font data could not be loaded");
        font.isMultiAtlasTexturesEnabled = true;
        if (UnityEngine.EventSystems.EventSystem.current == null)
            new GameObject("Connection Event System", typeof(UnityEngine.EventSystems.EventSystem), typeof(UnityEngine.EventSystems.StandaloneInputModule)).transform.SetParent(transform, false);

        var entry = Button(root.transform, "OpenConnection", "域名 / 服务器", 0, 56, 140, 36, () => panel.SetOpen(true));
        var entryRect = (RectTransform)entry.transform;
        entryRect.anchorMin = entryRect.anchorMax = new Vector2(1, 1); entryRect.pivot = new Vector2(1, 1); entryRect.anchoredPosition = new Vector2(-12, -56);
        blocker = Rect(root.transform, "ModalBlocker", 0, 0, 0, 0).gameObject;
        var blockerRect = (RectTransform)blocker.transform;
        blockerRect.anchorMin = Vector2.zero; blockerRect.anchorMax = Vector2.one; blockerRect.offsetMin = blockerRect.offsetMax = Vector2.zero;
        blocker.AddComponent<UnityEngine.UI.Image>().color = new Color(.015f, .025f, .04f, 1);
        surface = Rect(root.transform, "ConnectionModal", 0, 0, 640, 720).gameObject;
        var surfaceRect = (RectTransform)surface.transform;
        surfaceRect.anchorMin = surfaceRect.anchorMax = surfaceRect.pivot = new Vector2(.5f, 1);
        var cover = surface.AddComponent<UnityEngine.UI.Image>(); cover.color = new Color(.015f, .025f, .04f, .98f);
        // The entire modal catches clicks so the main menu below cannot also react.
        Text(surface.transform, "Title", "多人联机", 30, 22, 580, 34, 25);
        Button(surface.transform, "LegacyConnection", "原来的联机界面", 390, 22, 220, 32, () => panel.ShowLegacy());
        Text(surface.transform, "Subtitle", "域名 / IP / 樱花内网穿透 · 最多 8 人 · UDP", 30, 60, 580, 28, 16);
        nameField = Input(surface.transform, "Username", "用户名", 100, 24, value => panel.EditName(value));
        joinTab = Button(surface.transform, "JoinTab", "加入服务器", 30, 154, 280, 38, () => panel.SelectPage(0));
        hostTab = Button(surface.transform, "HostTab", "创建房间", 330, 154, 280, 38, () => panel.SelectPage(1));
        joinPage = Rect(surface.transform, "JoinPage", 0, 210, 640, 260).gameObject;
        addressField = Input(joinPage.transform, "Address", "服务器域名 / IP", 0, 300, value => panel.EditAddress(value));
        remotePortField = Input(joinPage.transform, "RemotePort", "服务器 / 远程端口", 48, 5, value => panel.EditJoinPort(value));
        Text(joinPage.transform, "Help", "种族和形象使用你在主菜单中的选择。\n域名:端口 / 樱花：节点域名 + 远程端口。\n局域网 / Radmin：房主 IP + 监听端口。", 30, 96, 580, 76, 17);
        observerChoice = Button(joinPage.transform, "ObserverChoice", "", 30, 178, 580, 28, () => {
            var selection = NetworkManager.singleton.GetComponent<CoopPlayerSelection>();
            selection.JoinAsObserver = !selection.JoinAsObserver;
        });
        connect = Button(joinPage.transform, "Connect", "连接服务器", 30, 210, 580, 40, () => panel.Connect());
        hostPage = Rect(surface.transform, "HostPage", 0, 210, 640, 290).gameObject;
        localPortField = Input(hostPage.transform, "LocalPort", "本地监听端口", 0, 5, value => panel.EditHostPort(value));
        Text(hostPage.transform, "Help", "房主电脑同时运行服务端与本地客户端。\n创建后由房主在主菜单开始游戏。\n\n樱花隧道：UDP / 本地地址 127.0.0.1\n本地端口：与上方监听端口一致。\n先启动隧道，再分享节点域名和远程端口。\n远程端口可以与本地端口不同。", 30, 48, 580, 176, 17);
        create = Button(hostPage.transform, "Create", "创建房间并监听", 30, 236, 580, 40, () => panel.CreateRoom());
        status = Text(surface.transform, "Status", "", 30, 510, 580, 78, 17);
        error = Text(surface.transform, "ValidationError", "", 30, 590, 580, 48, 17); error.color = new Color(1, .57f, .4f);
        stop = Button(surface.transform, "Disconnect", "断开联机", 30, 660, 280, 38, () => panel.Disconnect());
        stopLabel = stop.GetComponentInChildren<TMP_Text>();
        Button(surface.transform, "Close", "关闭页面", 330, 660, 280, 38, () => panel.SetOpen(false));
    }

    public void Refresh(bool open, int page, string username, string address, string remotePort, string localPort, string message)
    {
        surface.SetActive(open);
        blocker.SetActive(open);
        if (!open) return;
        var manager = NetworkManager.singleton as CoopNetworkManager;
        bool idle = manager != null && !manager.isNetworkActive;
        Sync(nameField, username, idle); Sync(addressField, address, idle);
        Sync(remotePortField, remotePort, idle); Sync(localPortField, localPort, idle);
        joinPage.SetActive(page == 0); hostPage.SetActive(page == 1);
        joinTab.interactable = idle; hostTab.interactable = idle; connect.interactable = idle; create.interactable = idle;
        observerChoice.interactable = idle;
        SetText(observerChoice.GetComponentInChildren<TMP_Text>(), manager.GetComponent<CoopPlayerSelection>().JoinAsObserver ? "身份：旁观者（点击切换）" : "身份：玩家（点击切换旁观）");
        joinTab.image.color = page == 0 ? new Color(.12f, .4f, .54f) : new Color(.12f, .17f, .23f);
        hostTab.image.color = page == 1 ? new Color(.12f, .4f, .54f) : new Color(.12f, .17f, .23f);
        stop.interactable = manager != null && !idle;
        if (manager != null)
        {
            bool pending = NetworkClient.active && !manager.ClientVerified && !NetworkServer.active;
            SetText(stopLabel, pending ? "取消连接" : "断开联机");
            string state = NetworkServer.active ? "你是房主 · UDP " + manager.KcpTransport.port + " · 人数 " + manager.VerifiedPlayers + "/8" :
                manager.ClientVerified ? "你不是房主（加入者） · RTT " + (NetworkTime.rtt * 1000).ToString("F0") + " ms" : pending ? "以加入者身份连接并校验版本……" : page == 1 ? "未连接 · 创建后你是房主" : "未连接 · 加入后你不是房主";
            SetText(status, state + "\n" + manager.StatusText + (manager.ClientVerified && !NetworkServer.active ? "\n房间设置由服务端同步" : ""));
        }
        SetText(error, message ?? "");
    }

    private static void Sync(TMP_InputField field, string value, bool editable)
    {
        if (field.text != (value ?? "")) field.SetTextWithoutNotify(value ?? "");
        field.interactable = editable;
    }
    internal void ValidateInvalidJoin()
    {
        addressField.text = "localhost"; remotePortField.text = "0";
        connect.onClick.Invoke();
    }
    internal void ValidateInvalidHost()
    {
        hostTab.onClick.Invoke(); localPortField.text = "0";
        create.onClick.Invoke();
    }
    private static void SetText(TMP_Text field, string text) { if (field.text != text) field.text = text; }
    private RectTransform Rect(Transform parent, string name, float x, float y, float width, float height)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height);
        return rect;
    }
    private TMP_Text Text(Transform parent, string name, string value, float x, float y, float width, float height, int size)
    {
        var label = Rect(parent, name, x, y, width, height).gameObject.AddComponent<TextMeshProUGUI>();
        label.font = font; label.fontSize = size; label.text = value; label.color = new Color(.9f, .94f, .98f);
        label.raycastTarget = false; label.richText = false; label.enableWordWrapping = true;
        return label;
    }
    private UnityEngine.UI.Button Button(Transform parent, string name, string caption, float x, float y, float width, float height, UnityEngine.Events.UnityAction action)
    {
        var rect = Rect(parent, name, x, y, width, height);
        var image = rect.gameObject.AddComponent<UnityEngine.UI.Image>(); image.color = new Color(.12f, .27f, .36f);
        var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>(); button.targetGraphic = image; button.onClick.AddListener(action);
        var text = Text(rect, "Label", caption, 8, 5, width - 16, height - 10, 18); text.alignment = TextAlignmentOptions.Center;
        return button;
    }
    private TMP_InputField Input(Transform parent, string name, string caption, float y, int limit, UnityEngine.Events.UnityAction<string> changed)
    {
        Text(parent, name + "Label", caption, 30, y + 7, 192, 30, 17);
        var rect = Rect(parent, name, 228, y, 382, 38);
        var image = rect.gameObject.AddComponent<UnityEngine.UI.Image>(); image.color = new Color(.12f, .17f, .23f);
        var input = rect.gameObject.AddComponent<TMP_InputField>();
        var viewport = Rect(rect, "Viewport", 10, 3, 362, 32); viewport.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
        var label = Text(viewport, "Value", "", 0, 0, 362, 32, 18);
        label.alignment = TextAlignmentOptions.MidlineLeft; label.enableWordWrapping = false;
        input.textViewport = viewport; input.textComponent = (TextMeshProUGUI)label; input.targetGraphic = image;
        input.characterLimit = limit; input.lineType = TMP_InputField.LineType.SingleLine;
        input.onValueChanged.AddListener(changed); return input;
    }
    private void OnDestroy()
    {
        if (font != null) { foreach (var texture in font.atlasTextures) if (texture != null) Destroy(texture); Destroy(font.material); Destroy(font); }
    }
}
