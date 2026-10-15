using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// Original weapon helpers contain single-player static animation/scope state. Preserve
// their exact routines, but give each remote actor a separate copy of those mutable values.
public sealed class CoopActorStatics : IDisposable
{
    private static readonly Dictionary<FieldInfo, object> initial = new Dictionary<FieldInfo, object>();
    private static readonly Dictionary<PlayerScript, Dictionary<FieldInfo, object>> actors = new Dictionary<PlayerScript, Dictionary<FieldInfo, object>>();
    private readonly Dictionary<FieldInfo, object> previous = new Dictionary<FieldInfo, object>();
    private readonly PlayerScript actor;
    public static void Initialize()
    {
        if (initial.Count != 0) return;
        foreach (Type type in typeof(CharacterStatus).Assembly.GetTypes())
        {
            if (!(type.Name.Contains("Skill") || type == typeof(SniperScope))) continue;
            foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                if (!field.IsLiteral && !field.IsInitOnly && (field.FieldType.IsPrimitive || field.FieldType == typeof(string) ||
                    field.FieldType == typeof(object) || field.FieldType == typeof(Coroutine) || field.FieldType == typeof(Vector3)))
                    initial[field] = field.GetValue(null);
        }
    }
    public CoopActorStatics(PlayerScript player)
    {
        Initialize(); actor = player;
        if (!actors.TryGetValue(player, out var values)) { values = new Dictionary<FieldInfo, object>(initial); actors[player] = values; }
        foreach (var pair in values) { previous[pair.Key] = pair.Key.GetValue(null); pair.Key.SetValue(null, pair.Value); }
    }
    public void Dispose()
    {
        var values = actors[actor];
        foreach (var pair in previous) { values[pair.Key] = pair.Key.GetValue(null); pair.Key.SetValue(null, pair.Value); }
    }
    public static void Release(PlayerScript actor) { actors.Remove(actor); }
}
