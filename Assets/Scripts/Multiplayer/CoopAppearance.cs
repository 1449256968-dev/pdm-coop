using Newtonsoft.Json.Linq;
using UnityEngine;
using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

public static class CoopAppearance
{
    // Capture runs for every non-actor entity on every snapshot (~300 entities at
    // ~10Hz). Reusing the stream/writer/renderer buffers and comparing shader
    // properties by id instead of by string keeps that work off the GC and off
    // Unity's name lookup.
    private static readonly MemoryStream captureStream = new MemoryStream(4096);
    private static readonly BinaryWriter captureWriter = new BinaryWriter(captureStream);
    private static readonly List<Renderer> rendererBuffer = new List<Renderer>();
    private static readonly List<Material> materialBuffer = new List<Material>();
    private static readonly List<Transform> pathBuffer = new List<Transform>();
    private static readonly int ColorProperty = Shader.PropertyToID("_Color");
    private static readonly int MainTexProperty = Shader.PropertyToID("_MainTex");

    // Capture native properties once on the Unity thread; construct JSON containers
    // only in the decoding worker. This removes per-renderer JSON allocations from capture.
    // The caller must consume the result before the next call on this thread.
    public static byte[] CaptureBinary(Component component, List<string> textureReferences)
    {
        captureStream.SetLength(0); captureStream.Position = 0;
        BinaryWriter writer = captureWriter;
        rendererBuffer.Clear();
        component.gameObject.GetComponentsInChildren(true, rendererBuffer);
        writer.Write(rendererBuffer.Count);
        foreach (Renderer renderer in rendererBuffer)
        {
            pathBuffer.Clear();
            for (Transform t = renderer.transform; t != component.transform; t = t.parent) pathBuffer.Add(t);
            writer.Write(pathBuffer.Count);
            for (int i = pathBuffer.Count - 1; i >= 0; i--) { writer.Write(pathBuffer[i].GetSiblingIndex()); writer.Write(pathBuffer[i].name); }
            writer.Write(renderer.GetType().FullName); writer.Write(renderer.enabled); writer.Write(renderer.gameObject.activeSelf);
            Write(writer, renderer.transform.localScale); Write(writer, renderer.transform.localPosition);
            Quaternion q = renderer.transform.localRotation; writer.Write(q.x); writer.Write(q.y); writer.Write(q.z); writer.Write(q.w);
            materialBuffer.Clear();
            renderer.GetSharedMaterials(materialBuffer);
            writer.Write(materialBuffer.Count);
            for (int m = 0; m < materialBuffer.Count; m++)
            {
                Material material = materialBuffer[m];
                bool color = material != null && material.HasProperty(ColorProperty); writer.Write(color);
                if (color) Write(writer, material.color);
                string texture = material != null && material.HasProperty(MainTexProperty) ? CoopTextureReference.Capture(material.mainTexture) : null;
                String(writer, texture); if (texture != null) textureReferences.Add(texture);
                String(writer, material != null && material.shader != null ? material.shader.name : null);
            }
            writer.Write(renderer is LineRenderer);
            if (renderer is LineRenderer line)
            {
                writer.Write(line.positionCount);
                for (int i = 0; i < line.positionCount; i++) Write(writer, line.GetPosition(i));
                Write(writer, line.startColor); Write(writer, line.endColor);
            }
        }
        writer.Flush();
        return captureStream.ToArray();
    }

    // ------------------------------------------------------------------
    // Unchanged-appearance reuse. Serializing every non-actor entity costs
    // ~9ms per snapshot on the host. A cheap 128-bit key over exactly the
    // values CaptureBinary writes lets an unchanged entity reuse its
    // previous payload, so receivers still get identical bytes.
    // ------------------------------------------------------------------
    private sealed class Cached { public byte[] bytes; public long keyA, keyB; public string[] textures; public int verifiedFrame; }
    private static readonly ConditionalWeakTable<Component, Cached> appearanceCache = new ConditionalWeakTable<Component, Cached>();
    private static readonly List<Renderer> keyRenderers = new List<Renderer>();
    private static readonly int[] keyPath = new int[64];
    private static long keyA, keyB;
    private const long KeySeedA = 1469598103934665603L;
    private const long KeySeedB = unchecked((long)0x9E3779B97F4A7C15UL);
    private const long KeyPrimeA = 1099511628211L;
    private const long KeyPrimeB = 0x100000001B3L;
    private static void Mix(long value)
    {
        unchecked
        {
            keyA = (keyA ^ value) * KeyPrimeA;
            keyB = (keyB ^ (value * KeySeedB)) * KeyPrimeB;
        }
    }
    private static void MixFloat(float value) => Mix(BitConverter.DoubleToInt64Bits(value));
    private static void MixVector(Vector3 value) { MixFloat(value.x); MixFloat(value.y); MixFloat(value.z); }
    private static void MixColour(Color value) { MixFloat(value.r); MixFloat(value.g); MixFloat(value.b); MixFloat(value.a); }
    public static void ResetCache() { appearanceCache.Clear(); }
    // Walks the same members as CaptureBinary. Returns true when a material samples a
    // RenderTexture, whose contents can change without any key member changing.
    private static bool CollectKey(Component component)
    {
        bool dynamic = false;
        keyRenderers.Clear();
        component.gameObject.GetComponentsInChildren(true, keyRenderers);
        Mix(keyRenderers.Count);
        for (int r = 0; r < keyRenderers.Count; r++)
        {
            Renderer renderer = keyRenderers[r];
            Mix(renderer.GetInstanceID());
            int depth = 0;
            for (Transform t = renderer.transform; t != component.transform && depth < keyPath.Length; t = t.parent) keyPath[depth++] = t.GetSiblingIndex();
            Mix(depth);
            for (int i = depth - 1; i >= 0; i--) Mix(keyPath[i]);
            Mix(renderer.GetType().FullName.GetHashCode());
            Mix(renderer.enabled ? 1 : 0); Mix(renderer.gameObject.activeSelf ? 1 : 0);
            MixVector(renderer.transform.localScale); MixVector(renderer.transform.localPosition);
            Quaternion rotation = renderer.transform.localRotation;
            MixFloat(rotation.x); MixFloat(rotation.y); MixFloat(rotation.z); MixFloat(rotation.w);
            materialBuffer.Clear();
            renderer.GetSharedMaterials(materialBuffer);
            Mix(materialBuffer.Count);
            for (int m = 0; m < materialBuffer.Count; m++)
            {
                Material material = materialBuffer[m];
                bool color = material != null && material.HasProperty(ColorProperty);
                Mix(color ? 1 : 0);
                if (color) MixColour(material.color);
                Texture texture = material != null && material.HasProperty(MainTexProperty) ? material.mainTexture : null;
                Mix(texture == null ? 0 : texture.GetInstanceID());
                // RenderTextures and unnamed generated textures can change pixels without any key
                // member changing, so their holder is always re-captured.
                if (CoopTextureReference.IsDynamicSource(texture)) dynamic = true;
                Mix(material != null && material.shader != null ? material.shader.GetInstanceID() : 0);
            }
            if (renderer is LineRenderer line)
            {
                Mix(line.positionCount);
                for (int i = 0; i < line.positionCount; i++) MixVector(line.GetPosition(i));
                MixColour(line.startColor); MixColour(line.endColor);
            }
        }
        return dynamic;
    }
    /// <summary>
    /// Appearance payload for one entity. Reuses the previous payload when the cheap key
    /// proves nothing CaptureBinary writes has changed; the referenced texture keys are
    /// always reported so generated-texture leases keep being renewed.
    /// </summary>
    public static byte[] CaptureCached(Component component, List<string> textureReferences)
    {
        // -coopCompatFullApply restores the pre-round behaviour of re-capturing every entity.
        if (CoopCompat.FullApply) return CaptureBinary(component, textureReferences);
        Cached entry = appearanceCache.GetOrCreateValue(component);
        keyA = KeySeedA; keyB = KeySeedB;
        bool dynamic = CollectKey(component);
        // The key must cover every member CaptureBinary writes; a full re-capture every ~2s
        // bounds the damage of any member it cannot cheaply cover (renames, in-place texture
        // edits) at roughly 5% of the un-cached capture cost.
        bool verify = Time.frameCount - entry.verifiedFrame >= 120;
        if (!dynamic && !verify && entry.bytes != null && entry.keyA == keyA && entry.keyB == keyB)
        {
            string[] cachedTextures = entry.textures;
            for (int i = 0; i < cachedTextures.Length; i++) textureReferences.Add(cachedTextures[i]);
            return entry.bytes;
        }
        var textures = new List<string>();
        byte[] bytes = CaptureBinary(component, textures);
        entry.bytes = bytes; entry.keyA = keyA; entry.keyB = keyB; entry.textures = textures.ToArray();
        entry.verifiedFrame = Time.frameCount;
        for (int i = 0; i < entry.textures.Length; i++) textureReferences.Add(entry.textures[i]);
        return bytes;
    }
    private static void String(BinaryWriter writer, string value) { writer.Write(value != null); if (value != null) writer.Write(value); }
    private static string String(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;
    private static void Write(BinaryWriter writer, Vector3 v) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
    private static void Write(BinaryWriter writer, Color c) { writer.Write(c.r); writer.Write(c.g); writer.Write(c.b); writer.Write(c.a); }
    private static JArray Floats(BinaryReader reader, int count)
    { var result = new JArray(); for (int i = 0; i < count; i++) result.Add(reader.ReadSingle()); return result; }
    private static int Count(BinaryReader reader, int limit)
    { int value = reader.ReadInt32(); if (value < 0 || value > limit) throw new InvalidDataException("Invalid appearance count"); return value; }
    public static JArray DecodeBinary(byte[] bytes)
    {
        using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream))
        {
            var result = new JArray(); int renderers = Count(reader, 4096);
            for (int r = 0; r < renderers; r++)
            {
                var path = new JArray(); var names = new JArray(); int depth = Count(reader, 128);
                for (int i = 0; i < depth; i++) { path.Add(reader.ReadInt32()); names.Add(reader.ReadString()); }
                var entry = new JObject { ["path"] = path, ["names"] = names, ["type"] = reader.ReadString(),
                    ["enabled"] = reader.ReadBoolean(), ["active"] = reader.ReadBoolean(),
                    ["scale"] = Floats(reader, 3), ["position"] = Floats(reader, 3), ["rotation"] = Floats(reader, 4) };
                var colors = new JArray(); var textures = new JArray(); var shaders = new JArray(); int materials = Count(reader, 256);
                for (int i = 0; i < materials; i++)
                {
                    colors.Add(reader.ReadBoolean() ? Floats(reader, 4) : null);
                    string texture = String(reader), shader = String(reader);
                    textures.Add(texture == null ? JValue.CreateNull() : new JValue(texture));
                    shaders.Add(shader == null ? JValue.CreateNull() : new JValue(shader));
                }
                entry["colors"] = colors; entry["textures"] = textures; entry["shaders"] = shaders;
                if (reader.ReadBoolean())
                {
                    var points = new JArray(); int count = Count(reader, 1024 * 1024);
                    for (int i = 0; i < count; i++) points.Add(Floats(reader, 3));
                    entry["line"] = points; entry["start"] = Floats(reader, 4); entry["end"] = Floats(reader, 4);
                }
                result.Add(entry);
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing appearance bytes");
            return result;
        }
    }
    private static JArray Vec(Vector3 v) => new JArray(v.x, v.y, v.z);
    private static JArray Colour(Color c) => new JArray(c.r, c.g, c.b, c.a);
    private static Color ReadColour(JToken v) => new Color((float)v[0], (float)v[1], (float)v[2], (float)v[3]);
    public static JArray Capture(Component component)
    {
        var result = new JArray();
        foreach (Renderer renderer in component.GetComponentsInChildren<Renderer>(true))
        {
            var path = new System.Collections.Generic.List<int>();
            var names = new System.Collections.Generic.List<string>();
            for (Transform t = renderer.transform; t != component.transform; t = t.parent) { path.Insert(0, t.GetSiblingIndex()); names.Insert(0, t.name); }
            Quaternion q = renderer.transform.localRotation;
            var entry = new JObject { ["path"] = new JArray(path), ["names"] = new JArray(names), ["type"] = renderer.GetType().FullName, ["enabled"] = renderer.enabled,
                ["active"] = renderer.gameObject.activeSelf, ["scale"] = Vec(renderer.transform.localScale),
                ["position"] = Vec(renderer.transform.localPosition), ["rotation"] = new JArray(q.x, q.y, q.z, q.w) };
            var colors = new JArray();
            foreach (Material material in renderer.sharedMaterials)
                colors.Add(material != null && material.HasProperty("_Color") ? Colour(material.color) : null);
            entry["colors"] = colors;
            entry["textures"] = new JArray(System.Array.ConvertAll(renderer.sharedMaterials,
                material => material != null && material.HasProperty("_MainTex") ? CoopTextureReference.Capture(material.mainTexture) : null));
            entry["shaders"] = new JArray(System.Array.ConvertAll(renderer.sharedMaterials,
                material => material != null && material.shader != null ? material.shader.name : null));
            if (renderer is LineRenderer line)
            {
                var points = new JArray();
                for (int i = 0; i < line.positionCount; i++) points.Add(Vec(line.GetPosition(i)));
                entry["line"] = points; entry["start"] = Colour(line.startColor); entry["end"] = Colour(line.endColor);
            }
            result.Add(entry);
        }
        return result;
    }
    public static void Apply(Component component, JArray entries, List<string> textureKeys = null)
    {
        foreach (JObject entry in entries)
        {
            Transform t = component.transform;
            int level = 0;
            foreach (JToken index in (JArray)entry["path"])
            {
                string name = (string)entry["names"][level++];
                Transform child = (int)index >= 0 && (int)index < t.childCount ? t.GetChild((int)index) : null;
                if (child == null || child.name != name)
                {
                    child = null;
                    for (int i = 0; i < t.childCount; i++) if (t.GetChild(i).name == name) { child = t.GetChild(i); break; }
                }
                t = child;
                if (t == null) break;
            }
            // Original animation helpers may add/remove cosmetic children between snapshots.
            if (t == null) continue;
            Renderer renderer = null;
            foreach (Renderer candidate in t.GetComponents<Renderer>())
                if (candidate.GetType().FullName == (string)entry["type"]) { renderer = candidate; break; }
            if (renderer == null) continue;
            // Every write below is guarded: the payload usually repeats the value the replica
            // already has, and re-assigning transform/material properties dirties Unity state
            // for no visible change.
            bool enabled = (bool)entry["enabled"];
            if (renderer.enabled != enabled) renderer.enabled = enabled;
            bool active = (bool)entry["active"];
            if (renderer.gameObject.activeSelf != active) renderer.gameObject.SetActive(active);
            JToken scale = entry["scale"];
            var scaleValue = new Vector3((float)scale[0], (float)scale[1], (float)scale[2]);
            if (t.localScale != scaleValue) t.localScale = scaleValue;
            JToken p = entry["position"], q = entry["rotation"];
            // Root pose was already applied in world space. Host entities can be
            // parented to a room while freshly instantiated replicas have no parent.
            if (t != component.transform)
            {
                var localPosition = new Vector3((float)p[0], (float)p[1], (float)p[2]);
                if (t.localPosition != localPosition) t.localPosition = localPosition;
                var localRotation = new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
                if (t.localRotation != localRotation) t.localRotation = localRotation;
            }
            materialBuffer.Clear();
            renderer.GetMaterials(materialBuffer);
            if (entry["shaders"] is JArray shaders)
                for (int i = 0; i < materialBuffer.Count && i < shaders.Count; i++)
                    if (shaders[i].Type == JTokenType.String && materialBuffer[i].shader.name != (string)shaders[i])
                    {
                        Shader shader = Shader.Find((string)shaders[i]);
                        if (shader != null) materialBuffer[i].shader = shader;
                    }
            JArray colors = (JArray)entry["colors"];
            for (int i = 0; i < materialBuffer.Count && i < colors.Count; i++)
                if (colors[i].Type != JTokenType.Null && materialBuffer[i].HasProperty(ColorProperty))
                {
                    Color colour = ReadColour(colors[i]);
                    if (materialBuffer[i].color != colour) materialBuffer[i].color = colour;
                }
            if (entry["textures"] is JArray textures)
                for (int i = 0; i < materialBuffer.Count && i < textures.Count; i++)
                    if (materialBuffer[i].HasProperty(MainTexProperty))
                    {
                        if (textureKeys != null && textures[i].Type == JTokenType.String) textureKeys.Add((string)textures[i]);
                        Texture texture = CoopTextureReference.Resolve((string)textures[i]);
                        if ((texture != null || textures[i].Type == JTokenType.Null) && materialBuffer[i].mainTexture != texture)
                            materialBuffer[i].mainTexture = texture;
                    }
            if (renderer is LineRenderer line && entry["line"] is JArray points)
            {
                line.positionCount = points.Count;
                for (int i = 0; i < points.Count; i++) line.SetPosition(i, new Vector3((float)points[i][0], (float)points[i][1], (float)points[i][2]));
                line.startColor = ReadColour(entry["start"]); line.endColor = ReadColour(entry["end"]);
            }
        }
    }
}
