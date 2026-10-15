using System.Collections.Generic;
using System.Reflection;
using System;
using System.Runtime.CompilerServices;
using UnityEngine;

// Preserve texture-backed sprite stack entries as well as string-backed entries.
// The original resource cache supplies the asset key, so no image pixels are transmitted.
public static class CoopTextureReference
{
    private static readonly Dictionary<Texture, string> known = new Dictionary<Texture, string>();
    private sealed class Pixels { public Pixels() { } public int frame; public string key; }
    private static readonly ConditionalWeakTable<Texture, Pixels> pixelSources = new ConditionalWeakTable<Texture, Pixels>();
    private static readonly Dictionary<string, Texture> pixelReplicas = new Dictionary<string, Texture>();
    private static readonly Dictionary<string, Texture> named = new Dictionary<string, Texture>();
    public static void Reset()
    {
        known.Clear(); named.Clear(); CoopTextureRecipe.Reset(); CoopAppearance.ResetCache();
        foreach (Texture texture in pixelReplicas.Values) if (texture != null) UnityEngine.Object.Destroy(texture);
        pixelReplicas.Clear();
    }
    public static string Capture(Texture texture)
    {
        if (texture == null) return null;
        string recipe = CoopTextureRecipe.Capture(texture);
        if (recipe != null) return recipe;
        if (texture is RenderTexture) return CapturePixels(texture);
        if (known.TryGetValue(texture, out string key)) return key;
        foreach (string name in new[] { "sprites", "customSprites", "textures", "spriteAnimationSets" })
        {
            object table = typeof(LoadedResourceManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            if (table is Dictionary<string, Sprite> sprites)
                foreach (var pair in sprites) if (pair.Value != null && pair.Value.texture == texture)
                { key = (name == "customSprites" ? "custom/" : "sprite/") + pair.Key; known[texture] = key; return key; }
            if (table is Dictionary<string, Texture> textures)
                foreach (var pair in textures) if (pair.Value == texture)
                { key = "texture/" + pair.Key; known[texture] = key; return key; }
            if (table is Dictionary<string, Sprite[]> animations)
                foreach (var pair in animations)
                    for (int i = 0; i < pair.Value.Length; i++) if (pair.Value[i].texture == texture)
                    { key = "animation/" + i + "/" + pair.Key; known[texture] = key; return key; }
        }
        if (texture is Texture2D && string.IsNullOrEmpty(texture.name)) return CapturePixels(texture);
        return "name/" + texture.name;
    }
    public static Texture Resolve(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (key.StartsWith("recipe/") || key.StartsWith("recipe-id/")) return CoopTextureRecipe.Resolve(key);
        if (key.StartsWith("pixels/"))
        {
            if (pixelReplicas.TryGetValue(key, out Texture existing) && existing != null) return existing;
            byte[] png = Convert.FromBase64String(key.Substring(7));
            if (png.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Generated sprite exceeds pixel payload limit");
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(png, true)) { UnityEngine.Object.Destroy(texture); throw new InvalidOperationException("Invalid generated sprite pixels"); }
            texture.name = "Coop Generated Sprite";
            pixelReplicas[key] = texture; known[texture] = key;
            return texture;
        }
        if (key.StartsWith("sprite/")) return LoadedResourceManager.GetSprite(key.Substring(7))?.texture;
        if (key.StartsWith("custom/")) return LoadedResourceManager.GetCustomSprite(key.Substring(7))?.texture;
        if (key.StartsWith("texture/")) return LoadedResourceManager.GetTexture(key.Substring(8));
        if (key.StartsWith("animation/"))
        {
            int end = key.IndexOf('/', 10);
            int index = int.Parse(key.Substring(10, end - 10));
            return LoadedResourceManager.GetSpriteAnimationSet(key.Substring(end + 1))[index].texture;
        }
        if (key.StartsWith("name/"))
        {
            if (named.TryGetValue(key, out Texture cached) && cached != null) return cached;
            foreach (Texture texture in Resources.FindObjectsOfTypeAll<Texture>())
                if (texture.name == key.Substring(5)) { named[key] = texture; return texture; }
        }
        return null;
    }
    private static string CapturePixels(Texture texture)
    {
        Pixels cache = pixelSources.GetOrCreateValue(texture);
        if (cache.key != null && cache.frame == Time.frameCount) return cache.key;
        if ((long)texture.width * texture.height > 16 * 1024 * 1024) throw new InvalidOperationException("Generated sprite exceeds pixel dimensions limit");
        RenderTexture old = RenderTexture.active;
        RenderTexture temporary = null;
        var image = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
        try
        {
            if (!(texture is RenderTexture))
            {
                temporary = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(texture, temporary);
            }
            RenderTexture.active = texture as RenderTexture ?? temporary;
            image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); image.Apply();
            cache.key = "pixels/" + Convert.ToBase64String(image.EncodeToPNG()); cache.frame = Time.frameCount;
            return cache.key;
        }
        finally
        {
            RenderTexture.active = old;
            if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
            UnityEngine.Object.Destroy(image);
        }
    }
    /// <summary>
    /// True when this texture's pixels can change without any field of its instance changing,
    /// so an appearance cache keyed on the texture reference must treat the holder as dynamic.
    /// Mirrors CoopTextureReference.Capture: only RenderTextures and unnamed Texture2Ds are
    /// re-encoded from pixels; every other texture resolves to a stable asset/recipe key, and
    /// marking those dynamic would re-capture every appearance on every snapshot.
    /// </summary>
    public static bool IsDynamicSource(Texture texture)
    {
        if (texture == null) return false;
        if (CoopTextureRecipe.Capture(texture) != null) return false;
        if (texture is RenderTexture) return true;
        if (known.ContainsKey(texture)) return false;
        return texture is Texture2D && string.IsNullOrEmpty(texture.name);
    }
    public static void PrunePixels(IEnumerable<Texture> retained)
    {
        var used = new HashSet<Texture>(retained);
        var stale = new List<string>();
        foreach (var pair in pixelReplicas) if (pair.Value == null || !used.Contains(pair.Value)) stale.Add(pair.Key);
        foreach (string key in stale)
        {
            Texture texture = pixelReplicas[key]; pixelReplicas.Remove(key); known.Remove(texture);
            if (texture != null) UnityEngine.Object.Destroy(texture);
        }
    }
}
