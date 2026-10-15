using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;

public struct CoopDialogueMessage : NetworkMessage { public uint token; public string text; public string[] options; public bool input; }
public struct CoopDialogueChoice : NetworkMessage { public uint token; public int option; public string text; }

// Callbacks remain on the host; only the prompt and a one-use selection token cross the wire.
public sealed class CoopDialogue : MonoBehaviour
{
    public static CoopDialogue Instance { get; private set; }
    private sealed class Pending { public uint token; public List<System.Action> callbacks; public PlayerScript actor; public Action<string> textCallback; }
    private readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
    private uint token;
    private void Awake() { Instance = this; }
    public void StartServer() { NetworkServer.RegisterHandler<CoopDialogueChoice>(Choose); pending.Clear(); }
    public void StopServer() { NetworkServer.UnregisterHandler<CoopDialogueChoice>(); pending.Clear(); }
    public void StartClient() { NetworkClient.RegisterHandler<CoopDialogueMessage>(Display); }
    public void StopClient() { NetworkClient.UnregisterHandler<CoopDialogueMessage>(); }
    public void Release(int id) { pending.Remove(id); }
    public static bool HasPending(int id) => Instance != null && Instance.pending.ContainsKey(id);
    public static bool TryShow(string text, List<System.Action> callbacks, List<string> options)
    {
        var actor = CoopActorContext.Current;
        if (actor == null || !actor.IsRemotePlayer || !NetworkServer.active || Instance == null) return false;
        if (!Instance.GetComponent<CoopSession>().TryGetConnection(actor.CoopPlayerId, out var connection)) return true;
        uint next = ++Instance.token;
        Instance.pending[actor.CoopPlayerId] = new Pending { actor = actor, token = next, callbacks = callbacks };
        connection.Send(new CoopDialogueMessage { token = next, text = text, options = options.ToArray() });
        Debug.Log("[Coop] Dialogue sent; player=" + actor.CoopPlayerId + "; token=" + next + "; choices=" + callbacks.Count);
        return true;
    }
    private void Display(CoopDialogueMessage message)
    {
        if (NetworkServer.active || !CoopReplication.IsReplica || message.options == null || message.options.Length == 0 || message.options.Length > 64) return;
        if (Environment.GetCommandLineArgs().Contains("-coopScenario"))
        {
            NetworkClient.Send(new CoopDialogueChoice { token = message.token, option = message.options.Length > 1 ? 1 : 0, text = "Probe" });
            Debug.Log("[CoopProbe] Client selected dialogue; token=" + message.token);
            return;
        }
        var callbacks = new List<System.Action>();
        if (message.input)
        {
            GameSystem.instance.SwapToAndFromMainGameUI(false);
            GameSystem.instance.textInputUI.ShowDisplay(message.text,
                () => { NetworkClient.Send(new CoopDialogueChoice { token = message.token, option = 0 }); GameSystem.instance.SwapToAndFromMainGameUI(true); },
                value => { NetworkClient.Send(new CoopDialogueChoice { token = message.token, option = 1, text = value }); GameSystem.instance.SwapToAndFromMainGameUI(true); },
                message.options[0], message.options[1]);
            return;
        }
        for (int i = 0; i < message.options.Length; i++)
        {
            int choice = i;
            callbacks.Add(() => { NetworkClient.Send(new CoopDialogueChoice { token = message.token, option = choice }); GameSystem.instance.SwapToAndFromMainGameUI(true); });
        }
        GameSystem.instance.SwapToAndFromMainGameUI(false);
        GameSystem.instance.multiOptionUI.ShowDisplay(message.text, callbacks, new List<string>(message.options));
    }
    private void Choose(NetworkConnectionToClient connection, CoopDialogueChoice choice)
    {
        int id = GetComponent<CoopSession>().GetPlayerId(connection);
        if (!pending.TryGetValue(id, out Pending p) || p.token != choice.token || choice.option < 0 || choice.option >= p.callbacks.Count || p.actor == null) return;
        pending.Remove(id);
        using (new CoopActorContext(p.actor))
        {
            if (p.textCallback != null && choice.option == 1)
            {
                if (choice.text == null || choice.text.Length > 512) return;
                p.textCallback(choice.text);
            }
            else p.callbacks[choice.option]?.Invoke();
        }
        Debug.Log("[Coop] Dialogue executed; player=" + id + "; token=" + choice.token + "; option=" + choice.option);
    }
    public static bool TryText(string text, System.Action cancel, Action<string> confirm, string cancelText, string confirmText)
    {
        var actor = CoopActorContext.Current;
        if (actor == null || !actor.IsRemotePlayer || !NetworkServer.active || Instance == null) return false;
        if (!Instance.GetComponent<CoopSession>().TryGetConnection(actor.CoopPlayerId, out var connection)) return true;
        uint next = ++Instance.token;
        Instance.pending[actor.CoopPlayerId] = new Pending { actor = actor, token = next, callbacks = new List<System.Action> { cancel, null }, textCallback = confirm };
        connection.Send(new CoopDialogueMessage { token = next, text = text, options = new[] { cancelText, confirmText }, input = true });
        return true;
    }
}
