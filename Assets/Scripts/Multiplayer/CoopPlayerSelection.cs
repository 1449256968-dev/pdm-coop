using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;

public struct CoopPlayerSelectionMessage : NetworkMessage
{
    public string species, imageSet;
    public bool observer;
}

// A player's menu choices are local profile data, never inferred from the host's avatar.
public sealed class CoopPlayerSelection : MonoBehaviour
{
    private readonly Dictionary<int, CoopPlayerSelectionMessage> selections = new Dictionary<int, CoopPlayerSelectionMessage>();
    private CoopPlayerSelectionMessage local;
    private int speciesIndex, imageIndex;
    private float nextPoll;
    public bool JoinAsObserver { get; set; }

    public static string Argument(string flag)
    {
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, flag);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
    public static NPCType ResolveSpecies(string name) => NPCType.startAsOptions.FirstOrDefault(t => t.name == name)
        ?? (name == Human.npcType.name ? Human.npcType : name == Male.npcType.name ? Male.npcType : null);

    public void CaptureLocal()
    {
        // A menu choice must not replace canonical species while snapshots still
        // refer to this world's definitions (including the intermission screen).
        // StartWorld rebuilds definitions when the next world actually begins.
        var world = GetComponent<CoopWorld>();
        if (world == null || !world.SharedWorldRunning || NPCType.types.Count == 0)
            NPCType.GenerateFullTypeList();
        var menu = GameSystem.instance.mainMenuUI;
        speciesIndex = menu.speciesChoice.value; imageIndex = menu.characterChoice.value;
        NPCType type = speciesIndex == 0 ? Human.npcType : speciesIndex == 1 ? Male.npcType :
            speciesIndex == 2 ? ExtendRandom.Random(NPCType.startAsOptions.Where(t => t != Human.npcType && t != Male.npcType)) :
            ResolveSpecies(menu.speciesChoice.options[speciesIndex].text);
        string requested = Argument("-coopSpecies");
        if (requested != null) type = ResolveSpecies(requested) ?? throw new InvalidOperationException("Unknown playable species: " + requested);
        var images = NPCType.humans.Where((name, i) => !GameSystem.settings.imageSetEnabled.Any(v => v) || GameSystem.settings.imageSetEnabled[i]).ToList();
        bool human = type == Human.npcType || type == Male.npcType;
        string image = imageIndex == 0 || (human && imageIndex == 1) ? ExtendRandom.Random(images) :
            imageIndex == 1 ? "" : menu.characterChoice.options[imageIndex].text;
        local = new CoopPlayerSelectionMessage { species = (type ?? Human.npcType).name, imageSet = image,
            observer = JoinAsObserver || Environment.GetCommandLineArgs().Contains("-coopObserver") };
    }
    public CoopPlayerSelectionMessage Local => local;
    public void SendLocal() { if (!NetworkServer.active) NetworkClient.Send(local); }
    public void StartServer() { selections.Clear(); NetworkServer.RegisterHandler<CoopPlayerSelectionMessage>(Receive); }
    public void StopServer() { NetworkServer.UnregisterHandler<CoopPlayerSelectionMessage>(); selections.Clear(); }
    public void Release(NetworkConnectionToClient connection) { selections.Remove(connection.connectionId); }
    public bool TryGet(NetworkConnectionToClient connection, out CoopPlayerSelectionMessage choice) => selections.TryGetValue(connection.connectionId, out choice);
    private void Receive(NetworkConnectionToClient connection, CoopPlayerSelectionMessage choice)
    {
        // Reject malformed/unplayable selections before they can reach an original-game constructor.
        if (choice.species == null || choice.species.Length > 128 || choice.imageSet == null || choice.imageSet.Length > 128 ||
            ResolveSpecies(choice.species) == null || (choice.imageSet != "" && !NPCType.humans.Contains(choice.imageSet)))
        { Debug.LogWarning("[Coop] Invalid player selection; connection=" + connection.connectionId); connection.Disconnect(); return; }
        selections[connection.connectionId] = choice;
        Debug.Log("[CoopSelection] Accepted; player=" + GetComponent<CoopSession>().GetPlayerId(connection) +
            "; species=" + choice.species + "; observer=" + choice.observer);
    }
    private void Update()
    {
        if (NetworkServer.active || !NetworkClient.isConnected || !NetworkClient.ready ||
            (GetComponent<CoopWorld>().SharedWorldRunning && !GameSystem.instance.postGame) || Time.unscaledTime < nextPoll) return;
        nextPoll = Time.unscaledTime + .25f;
        var menu = GameSystem.instance.mainMenuUI;
        if (menu.speciesChoice.value == speciesIndex && menu.characterChoice.value == imageIndex) return;
        CaptureLocal(); SendLocal();
    }
}
