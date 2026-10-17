using Mirror;
using UnityEngine;

/// <summary>In-game connection form, including UDP relay endpoints.</summary>
public sealed class CoopConnectionPanel : MonoBehaviour
{
    private static CoopConnectionPanel instance;
    public static bool IsOpen => instance != null && instance.open;
    public static bool BlocksLocalInput => (instance != null && instance.open) || CoopLegacyConnectionPanel.IsOpen;
    private string address, joinPort, hostPort, username, validationError;
    private bool open;
    private int page;
    private CoopConnectionView view;
    private InputWatcher registeredWatcher;
    private CoopNetworkManager Manager => NetworkManager.singleton as CoopNetworkManager;

    private void Start()
    {
        instance = this;
        view = gameObject.AddComponent<CoopConnectionView>();
        view.Initialize(this);
        username = CoopUsername.Local;
        address = PlayerPrefs.GetString("Coop.JoinAddress", "127.0.0.1");
        joinPort = PlayerPrefs.GetString("Coop.JoinPort", "7777");
        hostPort = PlayerPrefs.GetString("Coop.HostPort", "7777");
        open = CommandLineFlag("-coopConnectionPage") || CommandLineFlag("-coopOpenConnectionPage");
        if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-coopConnectionValidate") >= 0)
            StartCoroutine(ValidateForm());
        if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-coopConnectionRoleCapture") >= 0)
            StartCoroutine(ValidateRole());
    }

    private void Update()
    {
        // A form left open after joining suppresses EVERY local action (keys, look axes, action
        // edges, movement input and prediction), because BlocksLocalInput gates all of them. The
        // joiner fills the form, connects, and would otherwise never be able to play. Close the
        // form the moment the shared world starts; keep it usable if the player opens it later.
        bool running = WorldRunning();
        if (running && !worldWasRunning && !SelfTest)
        {
            open = false;
            CoopLegacyConnectionPanel.Hide();
        }
        worldWasRunning = running;
        if (view != null) view.Refresh(open, page, username, address, joinPort, hostPort, validationError);
        InputWatcher watcher = BlocksLocalInput && GameSystem.instance != null ? GameSystem.instance.inputWatcher : null;
        if (registeredWatcher != watcher)
        {
            if (registeredWatcher != null) registeredWatcher.inputWatchers.Remove(this);
            registeredWatcher = watcher;
        }
        // Use the game's existing modal-input gate: typing an address must not move or attack.
        if (watcher != null && !watcher.inputWatchers.Contains(this)) watcher.inputWatchers.Add(this);
    }
    private bool worldWasRunning;
    // Either signal is enough: the game world can be running before gameInProgress flips, and the
    // shared-world flag is what the replication layer itself uses.
    private static bool WorldRunning()
    {
        var game = GameSystem.instance;
        if (game != null && game.gameInProgress) return true;
        var world = game == null ? null : game.GetComponent<CoopWorld>();
        return world != null && world.SharedWorldRunning;
    }
    // The connection-page self tests keep the modal open on purpose; never auto-close for them.
    // -coopOpenConnectionPage opens the form without marking a self test, which is how the
    // "joined while the form was open" path is reproduced and verified.
    private static readonly bool SelfTest = CommandLineFlag("-coopConnectionPage") ||
        CommandLineFlag("-coopConnectionValidate") || CommandLineFlag("-coopConnectionRoleCapture");
    private static bool CommandLineFlag(string name) =>
        System.Array.IndexOf(System.Environment.GetCommandLineArgs(), name) >= 0;

    private void OnDisable()
    {
        if (registeredWatcher != null) registeredWatcher.inputWatchers.Remove(this);
        registeredWatcher = null;
    }

    internal void SetOpen(bool value)
    {
        open = value;
        if (!value) return;
        username = CoopUsername.Local;
        if (Manager != null && Manager.isNetworkActive && Manager.KcpTransport != null)
        {
            if (NetworkServer.active) hostPort = Manager.KcpTransport.port.ToString();
            else { address = Manager.networkAddress; joinPort = Manager.KcpTransport.port.ToString(); }
        }
        CoopLegacyConnectionPanel.Hide();
    }
    internal void ShowLegacy() { open = false; CoopLegacyConnectionPanel.Show(); }
    internal void SelectPage(int value) { page = value; validationError = null; }
    internal void EditName(string value) { username = value; CoopUsername.Local = value; }
    internal void EditAddress(string value) { address = value; validationError = null; }
    internal void EditJoinPort(string value) { joinPort = value; validationError = null; }
    internal void EditHostPort(string value) { hostPort = value; validationError = null; }
    internal void Connect() { if (Manager != null) Join(Manager); }
    internal void Disconnect()
    {
        if (Manager == null || !Manager.isNetworkActive) return;
        if (NetworkServer.active) Manager.StopHost(); else Manager.StopClient();
        validationError = null;
    }
    internal void CreateRoom()
    {
        if (Manager == null) return;
        if (!ushort.TryParse(hostPort, out ushort port) || port == 0) validationError = "本地监听端口必须为 1–65535。";
        else if (!Manager.StartHostOnPort(port)) validationError = "无法创建房间，请检查端口是否被占用。";
        else { validationError = null; PlayerPrefs.SetString("Coop.HostPort", port.ToString()); PlayerPrefs.Save(); }
    }

    private void Join(CoopNetworkManager manager)
    {
        if (!CoopEndpoint.TryParse(address, joinPort, out string host, out ushort port, out validationError)) return;
        if (!manager.JoinAddress(host, port)) { validationError = "无法开始连接，请先断开当前联机。"; return; }
        address = host; joinPort = port.ToString();
        PlayerPrefs.SetString("Coop.JoinAddress", address); PlayerPrefs.SetString("Coop.JoinPort", joinPort); PlayerPrefs.Save();
    }

    // Explicit test mode: exercise address validation and capture each page without saving settings.
    private System.Collections.IEnumerator ValidateForm()
    {
        Application.runInBackground = true;
        foreach (string target in new[] { "relay.example.com:34567", "127.0.0.1:34567", "localhost:34567", "[::1]:34567" })
            if (!CoopEndpoint.TryParse(target, "7777", out _, out ushort port, out _) || port != 34567)
                { Debug.LogWarning("[CoopConnectionTest] FAIL: Endpoint port precedence failed: " + target); yield break; }
        foreach (string target in new[] { "", "https://relay.example.com", "relay.example.com:0", "relay.example.com:65536", "bad address", "relay.example.com/path", "[::1" })
            if (CoopEndpoint.TryParse(target, "7777", out _, out _, out _))
                { Debug.LogWarning("[CoopConnectionTest] FAIL: Invalid endpoint accepted: " + target); yield break; }
        if (!CoopEndpoint.TryParse("relay.example.com", "45678", out _, out ushort separatePort, out _) || separatePort != 45678)
            { Debug.LogWarning("[CoopConnectionTest] FAIL: Separate remote port failed"); yield break; }
        open = true;
        view.ValidateInvalidJoin();
        if (string.IsNullOrEmpty(validationError) || Manager.isNetworkActive)
            { Debug.LogWarning("[CoopConnectionTest] FAIL: Invalid UI join started networking"); yield break; }
        string savedHostPort = hostPort;
        view.ValidateInvalidHost();
        if (validationError == null || !validationError.Contains("本地监听端口") || Manager.isNetworkActive)
            { Debug.LogWarning("[CoopConnectionTest] FAIL: Invalid UI host started networking"); yield break; }
        hostPort = savedHostPort; joinPort = "7777"; address = "relay.example.com:34567";
        PlayerMovementInput blocked = PlayerMovementInput.CaptureLocal(123);
        if (blocked.strafe != 0 || blocked.forward != 0 || blocked.run || blocked.yaw != 123)
            { Debug.LogWarning("[CoopConnectionTest] FAIL: Connection modal leaked movement input"); yield break; }
        string[] args = System.Environment.GetCommandLineArgs();
        int index = System.Array.IndexOf(args, "-logFile");
        string path = index >= 0 && index + 1 < args.Length ? args[index + 1] : System.IO.Path.Combine(Application.persistentDataPath, "connection");
        for (int view = 0; view < 3; view++)
        {
            page = view == 1 ? 1 : 0;
            validationError = view == 2 ? "端口必须为 1–65535；樱花用户请填写远程端口。" : null;
            yield return new WaitForSecondsRealtime(2);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(path + ".page" + view + ".png");
        }
        ShowLegacy();
        if (!BlocksLocalInput || !CoopLegacyConnectionPanel.IsOpen)
            { Debug.LogWarning("[CoopConnectionTest] FAIL: Legacy page did not open"); yield break; }
        if (GameSystem.instance != null) GameSystem.instance.mainMenuUI.CloseDisclaimer();
        yield return new WaitForSecondsRealtime(2);
        if (!CoopLegacyConnectionPanel.IsOpen)
            { Debug.LogWarning("[CoopConnectionTest] INCONCLUSIVE: panel changed during capture; rerun without manual input"); yield break; }
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(path + ".legacy.png");
        Debug.Log("[CoopConnectionTest] PASS: domain/IP, combined and separate remote ports, IPv6, invalid input, UI callbacks, modal movement gate, legacy page; four page captures requested");
    }

    private System.Collections.IEnumerator ValidateRole()
    {
        float deadline = Time.unscaledTime + 30;
        while (Manager == null || !Manager.ClientVerified)
        {
            if (Time.unscaledTime > deadline) { Debug.LogWarning("[CoopConnectionTest] FAIL: Role UI authentication timeout"); yield break; }
            yield return null;
        }
        string[] args = System.Environment.GetCommandLineArgs();
        bool expectedHost = System.Array.IndexOf(args, "-coopHost") >= 0;
        if (NetworkServer.active != expectedHost) { Debug.LogWarning("[CoopConnectionTest] FAIL: Connection role mismatch"); yield break; }
        int index = System.Array.IndexOf(args, "-logFile");
        string path = args[index + 1];
        SetOpen(true); page = expectedHost ? 1 : 0;
        yield return new WaitForSecondsRealtime(2);
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(path + ".role.png");
        ShowLegacy();
        if (GameSystem.instance != null) GameSystem.instance.mainMenuUI.CloseDisclaimer();
        yield return new WaitForSecondsRealtime(2);
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(path + ".legacy-role.png");
        Debug.Log("[CoopConnectionRoleTest] PASS: host=" + NetworkServer.active + "; authenticated=True; both panels captured");
    }
}
