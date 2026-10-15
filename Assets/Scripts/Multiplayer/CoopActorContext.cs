using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// An original player operation sees that actor's own camera and hidden UI, then restores
// the scene's local bindings even if the original code throws. No gameplay rules are duplicated.
public sealed class CoopActorContext : IDisposable
{
    public static PlayerScript Current { get; private set; }
    private sealed class Wiring
    {
        public readonly List<GameObject> roots = new List<GameObject>();
        public readonly Dictionary<FieldInfo, object> fields = new Dictionary<FieldInfo, object>();
    }
    private static readonly Dictionary<PlayerScript, Wiring> wiring = new Dictionary<PlayerScript, Wiring>();
    private readonly Dictionary<FieldInfo, object> saved = new Dictionary<FieldInfo, object>();
    private readonly PlayerScript previous;
    private readonly bool paused, actionPaused, inactive;
    private readonly int uiCount;
    private readonly float scale;
    private readonly bool cursor;
    private readonly byte controlPreferences;
    private readonly CursorLockMode cursorLock;
    private readonly CoopActorStatics staticState;
    private readonly Wiring actorWiring;
    public static void Attach(PlayerScript source, PlayerScript actor)
    {
        var game = GameSystem.instance;
        Transform root = source.colourFlash.transform.root;
        var roots = new HashSet<Transform> { root, game.observer.transform.root };
        foreach (FieldInfo field in typeof(GameSystem).GetFields(BindingFlags.Instance | BindingFlags.Public))
            if (field.GetValue(game) is MonoBehaviour ui && ui.GetType().Name.EndsWith("UI")) roots.Add(ui.transform.root);
        var mapping = new Dictionary<UnityEngine.Object, UnityEngine.Object>();
        var w = new Wiring();
        foreach (Transform original in roots)
        {
            var clone = UnityEngine.Object.Instantiate(original.gameObject);
            clone.name = "Coop Hidden " + original.name + " " + actor.CoopPlayerId;
            foreach (Canvas canvas in clone.GetComponentsInChildren<Canvas>(true)) canvas.enabled = false;
            foreach (Camera camera in clone.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
            foreach (AudioListener listener in clone.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
            foreach (MonoBehaviour b in clone.GetComponentsInChildren<MonoBehaviour>(true)) b.enabled = false;
            MapTree(original, clone.transform, mapping); w.roots.Add(clone);
        }
        MapTree(source.transform, actor.transform, mapping);
        foreach (FieldInfo f in typeof(PlayerScript).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object value = f.GetValue(source);
            if (value is UnityEngine.Object u && mapping.TryGetValue(u, out var replacement)) f.SetValue(actor, replacement);
            else if (value is IList list && f.FieldType.IsGenericType &&
                     typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType.GetGenericArguments()[0]))
            {
                var copy = (IList)Activator.CreateInstance(f.FieldType);
                foreach (object item in list)
                    copy.Add(item is UnityEngine.Object member && mapping.TryGetValue(member, out var mapped) ? mapped : item);
                f.SetValue(actor, copy);
            }
        }
        foreach (FieldInfo f in typeof(GameSystem).GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (f.GetValue(game) is UnityEngine.Object u && mapping.TryGetValue(u, out var replacement)) w.fields[f] = replacement;
        w.fields[typeof(GameSystem).GetField("player")] = actor;
        w.fields[typeof(GameSystem).GetField("playerTransform")] = actor.positionTransformReference;
        w.fields[typeof(GameSystem).GetField("playerRotationTransform")] = actor.rotationTransformReference;
        w.fields[typeof(GameSystem).GetField("activePrimaryCameraTransform")] = actor.rotationTransformReference;
        FieldInfo messages = typeof(GameSystem).GetField("messageLog", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo messageColors = typeof(GameSystem).GetField("messageColours", BindingFlags.Instance | BindingFlags.NonPublic);
        w.fields[messages] = new List<string>((List<string>)messages.GetValue(game));
        w.fields[messageColors] = new List<SaveableColour>((List<SaveableColour>)messageColors.GetValue(game));
        w.fields[typeof(GameSystem).GetField("playerClues")] = new List<string>(game.playerClues);
        var inputObject = new GameObject("Coop Hidden Input " + actor.CoopPlayerId);
        var input = inputObject.AddComponent<InputWatcher>();
        input.inputWatchers = new List<MonoBehaviour>();
        w.fields[typeof(GameSystem).GetField("inputWatcher")] = input;
        w.roots.Add(inputObject);
        wiring[actor] = w;
    }
    private static void MapTree(Transform original, Transform copy, Dictionary<UnityEngine.Object, UnityEngine.Object> mapping)
    {
        mapping[original.gameObject] = copy.gameObject;
        foreach (Component c in original.GetComponents<Component>())
            if (c != null) mapping[c] = copy.GetComponent(c.GetType());
        for (int i = 0; i < original.childCount; i++) MapTree(original.GetChild(i), copy.GetChild(i), mapping);
    }
    public static void Release(PlayerScript actor)
    {
        if (actor != null && wiring.TryGetValue(actor, out Wiring w))
        {
            foreach (GameObject root in w.roots) if (root != null) UnityEngine.Object.Destroy(root);
            wiring.Remove(actor); CoopActorStatics.Release(actor);
        }
    }
    public CoopActorContext(PlayerScript actor)
    {
        var game = GameSystem.instance;
        controlPreferences = CoopPlayerControl.Capture();
        if (actor.IsRemotePlayer) CoopPlayerControl.Apply(CoopPlayerControl.For(actor.CoopPlayerId));
        previous = Current; Current = actor;
        if (previous != actor) staticState = new CoopActorStatics(actor);
        paused = game.pauseAllInteraction; actionPaused = game.pauseActionOnly; inactive = game.playerInactive;
        uiCount = game.uisBeforeMainUI; scale = Time.timeScale; cursor = Cursor.visible; cursorLock = Cursor.lockState;
        if (wiring.TryGetValue(actor, out Wiring w))
        {
            actorWiring = w;
            foreach (var pair in w.fields) { saved[pair.Key] = pair.Key.GetValue(game); pair.Key.SetValue(game, pair.Value); }
        }
        game.playerInactive = !actor.gameObject.activeSelf;
    }
    public void Dispose()
    {
        var game = GameSystem.instance;
        foreach (var pair in saved)
        {
            if (actorWiring != null && (pair.Key.Name == "messageLog" || pair.Key.Name == "messageColours" || pair.Key.Name == "playerClues"))
                actorWiring.fields[pair.Key] = pair.Key.GetValue(game);
            pair.Key.SetValue(game, pair.Value);
        }
        game.pauseAllInteraction = paused; game.pauseActionOnly = actionPaused; game.playerInactive = inactive;
        game.uisBeforeMainUI = uiCount; Time.timeScale = scale; Cursor.visible = cursor; Cursor.lockState = cursorLock;
        CoopPlayerControl.Apply(controlPreferences);
        Current = previous;
        staticState?.Dispose();
    }
}
