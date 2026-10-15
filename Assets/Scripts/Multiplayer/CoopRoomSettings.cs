using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Mirror;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public struct CoopRoomSettingsMessage : NetworkMessage
{
    public uint revision;
    public byte[] compressedJson;
}

/// <summary>Only the host publishes room rules; local controls/audio/display stay local.</summary>
public sealed class CoopRoomSettings : MonoBehaviour
{
    private const int MaximumJsonBytes = 2 * 1024 * 1024;
    private static readonly FieldInfo[] RoomFields = FindRoomFields();
    private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        TypeNameHandling = TypeNameHandling.None,
        MaxDepth = 64,
        ContractResolver = new FieldContractResolver()
    });

    // The original game types expose their data as PUBLIC FIELDS, and Json.NET only serializes
    // properties by default. Without this resolver every nested rule object (DiplomacySettings,
    // monster/item sets, ...) travelled empty, which made a client unable to rebuild the world.
    private sealed class FieldContractResolver : DefaultContractResolver
    {
        // Setting MemberSerialization inside CreateObjectContract is too late: the member list has
        // already been built by then, which is why the field contract must be requested here.
        protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
        {
            return base.CreateProperties(type,
                typeof(UnityEngine.Object).IsAssignableFrom(type)
                    ? memberSerialization
                    : MemberSerialization.Fields);
        }
    }
    private string originalRoomJson;
    private string authoritativeJson;
    private string lastHostJson;
    private JObject lastHostTree, authoritativeTree;
    private byte[] lastHostPayload;
    private uint hostRevision;
    private uint clientRevision;
    private float nextPoll;
    private readonly HashSet<Selectable> lockedControls = new HashSet<Selectable>();
    private CoopNetworkManager Manager => GetComponent<CoopNetworkManager>();

    public static bool CanEdit => !NetworkClient.active || NetworkServer.active;
    public bool HasSnapshot => NetworkServer.active || authoritativeJson != null;
    public uint Revision => NetworkServer.active ? hostRevision : clientRevision;
    public uint ClientEnforcementCount { get; private set; }

    // These are original Settings fields. Visual/audio/keybinding/profile fields are excluded.
    private static FieldInfo[] FindRoomFields()
    {
        var local = new HashSet<string>(StringComparer.Ordinal)
        {
            "customName", "translationFile", "lookXAxis", "lookYAxis", "moveXAxis", "moveYAxis",
            "musicVolume", "sfxVolume", "mouseSensitivity", "thumbstickSensitivity",
            "tfCam", "showRangeIndicator", "readerMode", "showMinimap", "showCompass",
            "invertXLookAxis", "invertXMoveAxis", "centreMinimap", "firstRun", "limitFrameRate",
            "invertYAxis", "showCharacterNames", "showStatusBars", "clipDuringTFCam",
            "invertYMoveAxis", "autopilotHuman", "autopilotActive", "autopilotPassive", "autopilotAutoFacing", "showHostilesOnMinimap", "showNonHostilesOnMinimap",
            "silenceSoundsOnLoseFocus", "enableControllerInputs", "showCharacterNamesOnMap",
            "lockMinimapNorth", "keySettingsLists", "quickKeys", "negativeColour", "positiveColour",
            "dangerColour", "neutralColour", "discoveredRecipes", "settingsRolloverCode"
        };
        var fields = new List<FieldInfo>();
        foreach (var field in typeof(Settings).GetFields(BindingFlags.Instance | BindingFlags.Public))
            if (!local.Contains(field.Name)) fields.Add(field);
        fields.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return fields.ToArray();
    }

    public static string Capture(Settings settings)
    {
        var json = new JObject();
        foreach (var field in RoomFields)
        {
            object value = field.GetValue(settings);
            json[field.Name] = value == null ? JValue.CreateNull() : JToken.FromObject(value, Serializer);
        }
        return json.ToString(Formatting.None);
    }

    public static void Apply(Settings settings, string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new InvalidDataException("Room settings snapshot is too large.");
        JObject values;
        using (var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 64 })
            values = JObject.Load(reader);
        var candidate = new Settings();
        foreach (var field in RoomFields)
        {
            JToken token;
            if (!values.TryGetValue(field.Name, out token))
                throw new InvalidDataException("Missing room setting: " + field.Name);
            field.SetValue(candidate, token.ToObject(field.FieldType, Serializer));
        }
        if (candidate.gameplayRulesets == null || candidate.monsterRulesets == null ||
            candidate.diplomacySetups == null || candidate.itemSpawnSets == null ||
            candidate.latestGameplayRuleset < 0 || candidate.latestGameplayRuleset >= candidate.gameplayRulesets.Count ||
            candidate.latestMonsterRuleset < 0 || candidate.latestMonsterRuleset >= candidate.monsterRulesets.Count ||
            candidate.latestDiplomacySetup < 0 || candidate.latestDiplomacySetup >= candidate.diplomacySetups.Count ||
            candidate.latestItemSet < 0 || candidate.latestItemSet >= candidate.itemSpawnSets.Count)
            throw new InvalidDataException("Invalid room ruleset selection.");
        // Validate and decode everything before touching the live settings.
        foreach (var field in RoomFields) field.SetValue(settings, field.GetValue(candidate));
    }

    public void BeginClient()
    {
        if (GameSystem.settings != null) originalRoomJson = Capture(GameSystem.settings);
        authoritativeJson = null;
        authoritativeTree = null;
        clientRevision = 0;
        nextPoll = 0;
    }

    public void StartServer()
    {
        hostRevision = 0;
        lastHostJson = null;
        lastHostTree = null;
        lastHostPayload = null;
        nextPoll = 0;
    }

    public void StartClient()
    {
        NetworkClient.RegisterHandler<CoopRoomSettingsMessage>(OnSnapshot);
    }

    public void EndClient()
    {
        NetworkClient.UnregisterHandler<CoopRoomSettingsMessage>();
        if (originalRoomJson != null && GameSystem.settings != null)
            Apply(GameSystem.settings, originalRoomJson);
        originalRoomJson = authoritativeJson = null;
        authoritativeTree = null;
        clientRevision = 0;
        UnlockControls();
        RefreshMenu();
    }

    public static Settings ForLocalSave(Settings settings)
    {
        var manager = NetworkManager.singleton as CoopNetworkManager;
        var room = manager != null ? manager.GetComponent<CoopRoomSettings>() : null;
        if (CanEdit || room == null || room.originalRoomJson == null) return settings;
        // Keep personal edits but do not persist the host's room into the user's single-player defaults.
        var localCopy = JsonConvert.DeserializeObject<Settings>(JsonConvert.SerializeObject(settings));
        Apply(localCopy, room.originalRoomJson);
        return localCopy;
    }

    public void SendTo(NetworkConnectionToClient connection)
    {
        if (!NetworkServer.active || !connection.isAuthenticated || GameSystem.settings == null) return;
        if (UpdateHostSnapshot()) Broadcast();
        else connection.Send(new CoopRoomSettingsMessage { revision = hostRevision, compressedJson = lastHostPayload });
    }

    private void Broadcast()
    {
        foreach (var connection in NetworkServer.connections.Values)
            if (connection.isAuthenticated)
                connection.Send(new CoopRoomSettingsMessage { revision = hostRevision, compressedJson = lastHostPayload });
    }

    private bool UpdateHostSnapshot()
    {
        // No baseline yet means nothing to compare against; the first capture below creates it.
        if (lastHostTree != null && MatchesRoom(GameSystem.settings, lastHostTree)) return false;
        string json = Capture(GameSystem.settings);
        if (json == lastHostJson) return false;
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaximumJsonBytes) throw new InvalidDataException("Room settings snapshot is too large.");
        using (var buffer = new MemoryStream())
        {
            using (var gzip = new GZipStream(buffer, CompressionMode.Compress, true))
                gzip.Write(bytes, 0, bytes.Length);
            lastHostPayload = buffer.ToArray();
        }
        lastHostJson = json;
        lastHostTree = JObject.Parse(json);
        hostRevision++;
        return true;
    }

    private void OnSnapshot(CoopRoomSettingsMessage message)
    {
        if (NetworkServer.active || message.revision <= clientRevision) return;
        try
        {
            if (message.compressedJson == null || message.compressedJson.Length > MaximumJsonBytes)
                throw new InvalidDataException("Invalid room settings payload.");
            string json;
            using (var source = new MemoryStream(message.compressedJson))
            using (var gzip = new GZipStream(source, CompressionMode.Decompress))
            using (var result = new MemoryStream())
            {
                byte[] chunk = new byte[8192];
                int length;
                while ((length = gzip.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (result.Length + length > MaximumJsonBytes)
                        throw new InvalidDataException("Room settings decompression limit exceeded.");
                    result.Write(chunk, 0, length);
                }
                json = new UTF8Encoding(false, true).GetString(result.ToArray());
            }
            if (originalRoomJson == null) originalRoomJson = Capture(GameSystem.settings);
            Apply(GameSystem.settings, json);
            authoritativeJson = json;
            authoritativeTree = JObject.Parse(json);
            clientRevision = message.revision;
            RefreshMenu();
            LockControls();
            Debug.Log("[Coop] Room settings applied; revision=" + clientRevision + "; bytes=" + message.compressedJson.Length);
        }
        catch (Exception exception)
        {
            Manager.SetConnectionFailure("房主配置无法读取：" + exception.Message);
            Debug.LogError("[Coop] Room settings rejected: " + exception.Message);
            NetworkClient.Disconnect();
        }
    }

    private void LateUpdate()
    {
        if (GameSystem.settings == null || Time.unscaledTime < nextPoll) return;
        // The host only needs to notice a change to its own room settings; the client's tamper
        // rollback stays at one poll because that latency is a verification condition. Running
        // the 8ms deep comparison four times a second was a pure host main-thread spike.
        nextPoll = Time.unscaledTime + (NetworkServer.active ? 1f : 0.25f);
        if (NetworkServer.active)
        {
            if (UpdateHostSnapshot()) Broadcast();
        }
        else if (NetworkClient.active)
        {
            if (authoritativeTree != null && !MatchesRoom(GameSystem.settings, authoritativeTree))
                Apply(GameSystem.settings, authoritativeJson);
            ClientEnforcementCount++;
            LockControls();
        }
    }

    // The full deep comparison costs ~9ms on this settings tree and runs four times a second.
    // Splitting it across polls was tried and reverted: the scripted lifecycle regression proves
    // the client must roll a tampered rule back within one poll (<= 0.25s), which only a complete
    // comparison every poll can guarantee. The remaining cost is reduced through the compiled
    // member accessors in CoopSettingsComparison; a pre-compiled comparison plan is the next step
    // if this spike has to shrink further.
    private static bool MatchesRoom(Settings settings, JObject baseline)
    {
        long began = CoopPerformanceProbe.Begin("Coop.SettingsCompare");
        try
        {
            foreach (FieldInfo field in RoomFields)
                if (!CoopSettingsComparison.Matches(field.GetValue(settings), baseline[field.Name], Serializer.ContractResolver)) return false;
            return true;
        }
        finally { CoopPerformanceProbe.End("settingsCompare", began); }
    }

    private static bool IsNavigation(UnityEventBase callbacks)
    {
        for (int i = 0; i < callbacks.GetPersistentEventCount(); i++)
        {
            string method = callbacks.GetPersistentMethodName(i);
            if (method == "Back" || method == "NextXPage" || method == "PreviousXPage" ||
                method == "NextYPage" || method == "PreviousYPage" || method == "JumpTo" ||
                method == "SwapToGroupAffiliationsTab" || method == "SwapToSidesTab") return true;
        }
        return false;
    }

    private void LockControls()
    {
        var game = GameSystem.instance;
        if (game == null) return;
        foreach (var panel in new Component[] { game.settingsUI, game.spawnSettingsUI, game.itemSettingsUI, game.diplomacySettingsUI })
        {
            if (panel == null || !panel.gameObject.activeInHierarchy) continue;
            foreach (var selectable in panel.GetComponentsInChildren<Selectable>(true))
            {
                var button = selectable as Button;
                if (button != null && IsNavigation(button.onClick)) continue;
                Lock(selectable);
            }
        }
        var menu = game.mainMenuUI;
        if (menu != null && menu.gameObject.activeInHierarchy)
        {
            Lock(menu.mapSeedChoice);
            Lock(menu.spawnSetChoice);
            Lock(menu.itemSetChoice);
            Lock(menu.rulesetChoice);
            Lock(menu.diplomacySetupChoice);
            foreach (var button in menu.GetComponentsInChildren<Button>(true))
                for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
                {
                    string method = button.onClick.GetPersistentMethodName(i);
                    if (method == "ShowSettings" || method == "ShowSpawnSettings" ||
                        method == "ShowItemSettings" || method == "ShowDiplomacySettings" ||
                        method == "StartNewGame" || method == "StartTutorial" ||
                        method == "ResetAllSettings" || method == "ReloadData") Lock(button);
                }
        }
        var general = game.generalSettingsUI;
        if (general != null && general.gameObject.activeInHierarchy)
            foreach (var selectable in general.GetComponentsInChildren<Selectable>(true))
                if (CoopRoomSettingsGuard.IsRoomControl(selectable)) Lock(selectable);
    }

    private void Lock(Selectable control)
    {
        if (control != null && control.interactable)
        {
            lockedControls.Add(control);
            control.interactable = false;
        }
    }

    private void UnlockControls()
    {
        foreach (var control in lockedControls) if (control != null) control.interactable = true;
        lockedControls.Clear();
    }

    private static void RefreshMenu()
    {
        var game = GameSystem.instance;
        if (game == null || game.mainMenuUI == null) return;
        var menu = game.mainMenuUI;
        menu.spawnSetChoice.ClearOptions();
        menu.spawnSetChoice.AddOptions(GameSystem.settings.monsterRulesets.ConvertAll(r => r.name));
        menu.spawnSetChoice.SetValueWithoutNotify(GameSystem.settings.latestMonsterRuleset);
        menu.itemSetChoice.ClearOptions();
        menu.itemSetChoice.AddOptions(GameSystem.settings.itemSpawnSetNames);
        menu.itemSetChoice.SetValueWithoutNotify(GameSystem.settings.latestItemSet);
        menu.rulesetChoice.ClearOptions();
        menu.rulesetChoice.AddOptions(GameSystem.settings.gameplayRulesets.ConvertAll(r => r.name));
        menu.rulesetChoice.SetValueWithoutNotify(GameSystem.settings.latestGameplayRuleset);
        menu.diplomacySetupChoice.ClearOptions();
        menu.diplomacySetupChoice.AddOptions(GameSystem.settings.diplomacySetups.ConvertAll(r => r.name));
        menu.diplomacySetupChoice.SetValueWithoutNotify(GameSystem.settings.latestDiplomacySetup);
        menu.mapSeedChoice.SetTextWithoutNotify(GameSystem.settings.mapSeed);
        if (game.generalSettingsUI != null && game.generalSettingsUI.gameObject.activeInHierarchy)
            game.generalSettingsUI.UpdateDisplay();
        if (game.settingsUI != null && game.settingsUI.gameObject.activeInHierarchy)
        {
            game.settingsUI.rulesetChoice.ClearOptions();
            game.settingsUI.rulesetChoice.AddOptions(GameSystem.settings.gameplayRulesets.ConvertAll(r => r.name));
            game.settingsUI.rulesetChoice.SetValueWithoutNotify(GameSystem.settings.latestGameplayRuleset);
            game.settingsUI.UpdateDisplay();
        }
        if (game.spawnSettingsUI != null && game.spawnSettingsUI.gameObject.activeInHierarchy)
        {
            game.spawnSettingsUI.rulesetChoice.ClearOptions();
            game.spawnSettingsUI.rulesetChoice.AddOptions(GameSystem.settings.monsterRulesets.ConvertAll(r => r.name));
            game.spawnSettingsUI.rulesetChoice.SetValueWithoutNotify(GameSystem.settings.latestMonsterRuleset);
            game.spawnSettingsUI.UpdateDisplay();
        }
        if (game.itemSettingsUI != null && game.itemSettingsUI.gameObject.activeInHierarchy)
        {
            game.itemSettingsUI.spawnSetChoice.ClearOptions();
            game.itemSettingsUI.spawnSetChoice.AddOptions(GameSystem.settings.itemSpawnSetNames);
            game.itemSettingsUI.spawnSetChoice.SetValueWithoutNotify(GameSystem.settings.latestItemSet);
            game.itemSettingsUI.UpdateSliders();
        }
        if (game.diplomacySettingsUI != null && game.diplomacySettingsUI.gameObject.activeInHierarchy)
        {
            game.diplomacySettingsUI.diplomacySetChoice.ClearOptions();
            game.diplomacySettingsUI.diplomacySetChoice.AddOptions(GameSystem.settings.diplomacySetups.ConvertAll(r => r.name));
            game.diplomacySettingsUI.diplomacySetChoice.SetValueWithoutNotify(GameSystem.settings.latestDiplomacySetup);
            game.diplomacySettingsUI.UpdateDisplay();
        }
    }
}
