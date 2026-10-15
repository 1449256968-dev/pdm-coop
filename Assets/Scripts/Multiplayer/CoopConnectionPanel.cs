using System;
using Mirror;
using UnityEngine;

/// <summary>Small direct-IP control panel for LAN and port-forwarded Internet sessions.</summary>
public sealed class CoopConnectionPanel : MonoBehaviour
{
    private const int MaxPlayers = CoopNetworkManager.MaxPlayersIncludingHost;
    private string address = "127.0.0.1";
    private string portText = CoopNetworkManager.DefaultPort.ToString();
    private string validationError;
    private bool open;
    private string username;
    private void Start() { username = CoopUsername.Local; }

    private CoopNetworkManager Manager => NetworkManager.singleton as CoopNetworkManager;

    private void OnGUI()
    {
        if (!open)
        {
            if (GUI.Button(new Rect(Screen.width - 112, 12, 100, 32), "联机"))
                open = true;
            return;
        }

        GUI.Window(891201, new Rect(16, 48, 390, 390), DrawWindow, "局域网 / 直连联机");
    }

    private void DrawWindow(int windowId)
    {
        var manager = Manager;
        if (manager == null)
        {
            GUILayout.Label("联机管理器尚未就绪。");
            return;
        }

        GUILayout.Label("最多 8 人（房主 + 7 名加入者），UDP 端口默认 7777");
        bool nameEnabled = GUI.enabled;
        GUI.enabled = nameEnabled && !manager.isNetworkActive;
        GUILayout.BeginHorizontal();
        GUILayout.Label("用户名", GUILayout.Width(72));
        string editedName = GUILayout.TextField(username ?? "", CoopUsername.MaxLength);
        if (editedName != username) { username = editedName; CoopUsername.Local = username; }
        GUILayout.EndHorizontal();
        GUI.enabled = nameEnabled;
        GUILayout.Label("入房前设置，最多 24 字；留空将使用默认玩家名。");
        GUILayout.BeginHorizontal();
        GUILayout.Label("房主端口", GUILayout.Width(72));
        portText = GUILayout.TextField(portText);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        bool previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && !manager.isNetworkActive;
        if (GUILayout.Button("创建房间"))
        {
            validationError = TryGetPort(out ushort port) && manager.StartHostOnPort(port)
                ? null : "无法启动房间。端口必须为 1–65535。";
        }
        GUI.enabled = previousEnabled && manager.isNetworkActive;
        if (GUILayout.Button("断开"))
        {
            if (manager.isNetworkActive)
            {
                if (NetworkServer.active)
                    manager.StopHost();
                else
                    manager.StopClient();
                validationError = null;
            }
        }
        GUI.enabled = previousEnabled;
        GUILayout.EndHorizontal();

        GUILayout.Space(8);
        GUILayout.BeginHorizontal();
        GUILayout.Label("房主 IP", GUILayout.Width(72));
        address = GUILayout.TextField(address);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label("端口", GUILayout.Width(72));
        portText = GUILayout.TextField(portText);
        GUI.enabled = previousEnabled && !manager.isNetworkActive;
        if (GUILayout.Button("加入房间", GUILayout.Width(92)))
        {
            validationError = TryGetPort(out ushort port) && manager.JoinAddress(address, port)
                ? null : "无法连接。请填写房主 IP 和 1–65535 的端口。";
        }
        GUI.enabled = previousEnabled;
        GUILayout.EndHorizontal();

        string state = NetworkServer.active ? "房主" : manager.ClientVerified ? "客户端已验证" : NetworkClient.isConnected ? "客户端校验中" : NetworkClient.active ? "客户端连接中" : "未连接";
        int connected = NetworkServer.active ? manager.VerifiedPlayers : 0;
        GUILayout.Space(6);
        GUILayout.Label("状态：" + state + (NetworkServer.active ? "；已验证人数：" + connected + " / " + MaxPlayers : ""));
        GUILayout.Label(manager.StatusText);
        var roomSettings = manager.GetComponent<CoopRoomSettings>();
        if (manager.isNetworkActive && roomSettings != null)
            GUILayout.Label(NetworkServer.active ? "房间设置：仅房主可修改" : roomSettings.HasSnapshot
                ? "房间设置：已同步，只读（版本 " + roomSettings.Revision + "）" : "房间设置：等待房主同步……");
        if (!string.IsNullOrEmpty(validationError))
            GUILayout.Label(validationError);
        GUILayout.Label("公网直连要求房主在路由器和系统防火墙放行所选 UDP 端口。", GUILayout.Height(30));
        if (GUILayout.Button("关闭面板", GUILayout.Width(100)))
            open = false;
        GUI.DragWindow(new Rect(0, 0, 10000, 24));
    }

    private bool TryGetPort(out ushort port)
    {
        return ushort.TryParse(portText, out port) && port > 0;
    }
}


