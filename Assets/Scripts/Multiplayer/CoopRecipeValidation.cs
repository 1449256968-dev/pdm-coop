using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Opt-in GPU regression for cumulative transformation recipes. Keep it outside FPS
// measurements: this deliberately reads back rendered pixels.
public sealed class CoopRecipeValidation : MonoBehaviour
{
    private bool done;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Environment.GetCommandLineArgs().Contains("-coopRecipeValidate"))
            new GameObject("Coop Recipe Validation").AddComponent<CoopRecipeValidation>();
    }
    private void Update()
    {
        if (done || GameSystem.instance == null || !GameSystem.instance.gameInProgress) return;
        done = true;
        var image = new Texture2D(16, 16, TextureFormat.RGBA32, false);
        var mask = new Texture2D(16, 16, TextureFormat.RGBA32, false);
        var pixels = Enumerable.Range(0, 256).Select(i => new Color((i % 16) / 15f, (i / 16) / 15f, .25f, 1)).ToArray();
        image.SetPixels(pixels); image.Apply(); mask.SetPixels(pixels); mask.Apply();
        Texture current = image;
        try
        {
            for (int i = 0; i < 128; i++)
            {
                Texture old = current;
                current = RenderFunctions.DissolveTransformation(.01f + i / 256f, current, mask);
                if (old != image) Destroy(old);
                string key = CoopTextureReference.Capture(current);
                if (key == null || key.Length != 74) throw new InvalidOperationException("Generated texture reference grows with history");
            }
            string finalKey = CoopTextureReference.Capture(current);
            var closure = CoopTextureRecipe.Collect(new JArray(finalKey));
            if (closure.Count != 128 || closure.Values.Sum(b => b.Length) > 256 * 1024)
                throw new InvalidOperationException("Cumulative recipe storage is not bounded by node count");
            Texture replica = CoopTextureRecipe.Resolve(finalKey);
            if (CoopTextureRecipe.PixelSignature(current) != CoopTextureRecipe.PixelSignature(replica))
                throw new InvalidOperationException("Cumulative recipe reconstruction changes rendered pixels");
            Debug.Log("[CoopRecipeTest] PASS: 128 cumulative frames; fixed reference length; linear storage; rendered pixels identical; bytes=" + closure.Values.Sum(b => b.Length));
        }
        finally { Destroy(current); Destroy(image); Destroy(mask); }
        enabled = false;
    }
}
