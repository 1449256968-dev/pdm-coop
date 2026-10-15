using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Security.Cryptography;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Generated transformation images have no asset name. Replicate the original
// render operation and its inputs rather than looking up an unnamed texture.
public static class CoopTextureRecipe
{
    private sealed class Recipe { public string key; public byte[] payload; public JObject data; public float lastUse; public readonly List<Recipe> parents = new List<Recipe>(); }
    private static float nextPrune;
    private static readonly ConditionalWeakTable<Texture, Recipe> recipes = new ConditionalWeakTable<Texture, Recipe>();
    private static readonly Dictionary<string, Texture> resolved = new Dictionary<string, Texture>();
    private static readonly Dictionary<string, Recipe> definitions = new Dictionary<string, Recipe>();
    public static void Reset()
    {
        foreach (Texture texture in resolved.Values) if (texture != null) UnityEngine.Object.Destroy(texture);
        resolved.Clear(); definitions.Clear();
    }
    private static string Key(byte[] payload)
    {
        using (var hash = SHA256.Create()) return "recipe-id/" + BitConverter.ToString(hash.ComputeHash(payload)).Replace("-", "");
    }
    private static void Ensure(Recipe root)
    {
        var queue = new Stack<Recipe>(); queue.Push(root);
        while (queue.Count != 0)
        {
            Recipe recipe = queue.Pop();
            if (definitions.ContainsKey(recipe.key)) continue;
            definitions[recipe.key] = recipe;
            foreach (Recipe parent in recipe.parents) queue.Push(parent);
        }
    }
    public static RenderTexture Record(RenderTexture texture, string operation, params object[] args)
    {
        var values = new JArray();
        var parents = new List<Recipe>();
        foreach (object value in args)
        {
            if (value is Texture input)
            {
                values.Add(CoopTextureReference.Capture(input));
                if (recipes.TryGetValue(input, out Recipe parent)) parents.Add(parent);
            }
            else if (value is List<Texture> list)
            {
                values.Add(new JArray(list.ConvertAll(CoopTextureReference.Capture)));
                foreach (Texture image in list) if (image != null && recipes.TryGetValue(image, out Recipe parent)) parents.Add(parent);
            }
            else values.Add(JToken.FromObject(value));
        }
        var data = new JObject { ["op"] = operation, ["args"] = values };
        byte[] payload = Encoding.UTF8.GetBytes(data.ToString(Formatting.None));
        var node = new Recipe { key = Key(payload), payload = payload, data = data }; node.parents.AddRange(parents);
        recipes.Remove(texture); recipes.Add(texture, node); Ensure(node);
        return texture;
    }
    public static string Capture(Texture texture)
    {
        if (!recipes.TryGetValue(texture, out Recipe recipe)) return null;
        Ensure(recipe); return recipe.key;
    }
    public static Dictionary<string, byte[]> Collect(JToken root)
    {
        var result = new Dictionary<string, byte[]>();
        var tokens = new Stack<JToken>(); tokens.Push(root);
        var keys = new Queue<string>();
        while (tokens.Count != 0)
        {
            JToken token = tokens.Pop();
            if (token.Type == JTokenType.String)
            {
                string key = (string)token; if (key != null && key.StartsWith("recipe-id/")) keys.Enqueue(key);
            }
            else if (token is JContainer container) foreach (JToken child in container.Children()) tokens.Push(child);
        }
        return Collect(keys);
    }
    public static Dictionary<string, byte[]> Collect(IEnumerable<string> references)
    {
        var result = new Dictionary<string, byte[]>();
        var keys = new Queue<string>(references.Where(key => key != null && key.StartsWith("recipe-id/")));
        while (keys.Count != 0)
        {
            string key = keys.Dequeue(); if (result.ContainsKey(key)) continue;
            if (!definitions.TryGetValue(key, out Recipe recipe)) throw new InvalidOperationException("Missing generated texture definition " + key);
            result[key] = recipe.payload;
            recipe.lastUse = Time.realtimeSinceStartup;
            foreach (JValue value in ((JArray)recipe.data["args"]).DescendantsAndSelf().OfType<JValue>())
                if (value.Type == JTokenType.String && ((string)value)?.StartsWith("recipe-id/") == true)
                {
                    string parentKey = (string)value;
                    if (!definitions.ContainsKey(parentKey))
                    {
                        Recipe parent = recipe.parents.FirstOrDefault(p => p.key == parentKey);
                        if (parent != null) Ensure(parent);
                    }
                    keys.Enqueue(parentKey);
                }
        }
        if (Time.realtimeSinceStartup >= nextPrune)
        {
            nextPrune = Time.realtimeSinceStartup + 30;
            foreach (string key in definitions.Where(p => Time.realtimeSinceStartup - p.Value.lastUse > 60).Select(p => p.Key).ToArray()) definitions.Remove(key);
        }
        return result;
    }
    public static void Register(string key, byte[] payload)
    {
        if (payload == null || payload.Length > 8 * 1024 * 1024 || key != Key(payload)) throw new InvalidOperationException("Invalid generated texture definition");
        if (definitions.TryGetValue(key, out Recipe existing)) { existing.lastUse = Time.realtimeSinceStartup; return; }
        var data = JObject.Parse(new UTF8Encoding(false, true).GetString(payload));
        if (!(data["args"] is JArray) || data["op"]?.Type != JTokenType.String) throw new InvalidOperationException("Invalid generated texture operation");
        definitions[key] = new Recipe { key = key, payload = payload, data = data, lastUse = Time.realtimeSinceStartup };
    }
    public static bool HasDefinition(string key) => definitions.ContainsKey(key);
    public static Texture Resolve(string key)
    {
        if (resolved.TryGetValue(key, out Texture existing) && existing != null) return existing;
        JObject recipe;
        if (key.StartsWith("recipe-id/"))
        {
            if (!definitions.TryGetValue(key, out Recipe definition)) throw new InvalidOperationException("Missing generated texture definition " + key);
            recipe = definition.data;
        }
        else recipe = JObject.Parse(key.Substring(7));
        JArray a = (JArray)recipe["args"];
        Texture result;
        switch ((string)recipe["op"])
        {
            case "stitch": result = RenderFunctions.StitchCharacters((string)a[0], (string)a[1], (int)a[2], (string)a[3], (string)a[4], (int)a[5], (string)a[6]); break;
            case "fade": result = RenderFunctions.FadeImages(T(a[0]), T(a[1]), (float)a[2]); break;
            case "fadeOld": result = RenderFunctions.FadeImagesOldDisappears(T(a[0]), T(a[1]), (float)a[2]); break;
            case "stack":
                var images = new List<Texture>();
                foreach (JToken item in (JArray)a[0]) images.Add(T(item));
                result = RenderFunctions.StackImages(images, (bool)a[1]); break;
            case "cross": result = RenderFunctions.CrossDissolveImage((float)a[0], T(a[1]), T(a[2]), T(a[3])); break;
            case "dissolve": result = RenderFunctions.DissolveTransformation((float)a[0], T(a[1]), T(a[2])); break;
            case "dissolvePlus": result = RenderFunctions.DissolveTransformationPlus((float)a[0], T(a[1]), T(a[2])); break;
            case "tie": result = RenderFunctions.CoopWeremouseTie((string)a[0], new Color((float)a[1], (float)a[2], (float)a[3], (float)a[4])); break;
            default: throw new InvalidOperationException("Unsupported generated texture operation: " + recipe["op"]);
        }
        // Character presentation owns and releases these textures; cache only references.
        resolved[key] = result;
        return result;
    }
    public static void PruneResolved(IEnumerable<Texture> retained)
    {
        var used = new HashSet<Texture>(retained);
        var stale = new List<string>();
        foreach (var pair in resolved) if (pair.Value == null || !used.Contains(pair.Value)) stale.Add(pair.Key);
        foreach (string key in stale)
        {
            Texture texture = resolved[key]; resolved.Remove(key);
            if (texture != null) UnityEngine.Object.Destroy(texture);
        }
    }
    private static Texture T(JToken key)
    {
        Texture texture = CoopTextureReference.Resolve((string)key);
        if (texture == null) throw new InvalidOperationException("Missing generated texture input: " + key);
        return texture;
    }
    public static string PixelSignature(Texture texture)
    {
        if (texture == null) return "missing";
        RenderTexture old = RenderTexture.active;
        RenderTexture target = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGB32);
        Texture2D pixels = new Texture2D(64, 64, TextureFormat.RGBA32, false);
        try
        {
            Graphics.Blit(texture, target);
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); pixels.Apply();
            using (var digest = System.Security.Cryptography.SHA256.Create())
                return Convert.ToBase64String(digest.ComputeHash(pixels.GetRawTextureData()));
        }
        finally
        {
            RenderTexture.active = old;
            RenderTexture.ReleaseTemporary(target);
            UnityEngine.Object.Destroy(pixels);
        }
    }
}
