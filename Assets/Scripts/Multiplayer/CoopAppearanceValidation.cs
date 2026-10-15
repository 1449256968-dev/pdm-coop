using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

public sealed class CoopAppearanceValidation : MonoBehaviour
{
    private bool done;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Setup()
    {
        if (Environment.GetCommandLineArgs().Contains("-coopAppearanceValidate"))
            new GameObject("Coop Appearance Validation").AddComponent<CoopAppearanceValidation>();
    }
    private void Update()
    {
        var game = GameSystem.instance;
        if (done || game == null || !game.gameInProgress) return;
        done = true;
        var components = new HashSet<Component>(game.strikeableLocations.Cast<Component>());
        foreach (var list in game.ActiveObjects.Values) foreach (Component component in list)
            if (component != null && component.gameObject.activeInHierarchy) components.Add(component);
        components.Add(game.player);
        int count = 0;
        foreach (Component component in components)
        {
            if (component == null) continue;
            JArray expected = CoopAppearance.Capture(component);
            byte[] bytes = CoopAppearance.CaptureBinary(component, new List<string>());
            JArray actual = CoopAppearance.DecodeBinary(bytes);
            if (!JToken.DeepEquals(expected, actual))
            {
                string detail = "renderer count " + expected.Count + "/" + actual.Count;
                for (int i = 0; i < Math.Min(expected.Count, actual.Count); i++)
                    foreach (JProperty property in ((JObject)expected[i]).Properties())
                        if (!JToken.DeepEquals(property.Value, actual[i][property.Name]))
                        {
                            string left = property.Value.ToString(Newtonsoft.Json.Formatting.None);
                            string right = actual[i][property.Name]?.ToString(Newtonsoft.Json.Formatting.None) ?? "missing";
                            detail = property.Name + "; expected=" + left.Substring(0, Math.Min(200, left.Length)) + "; actual=" + right.Substring(0, Math.Min(200, right.Length));
                            break;
                        }
                throw new InvalidOperationException("Binary appearance differs from the original schema: " + component.GetType() + "; " + detail);
            }
            count++;
        }
        Debug.Log("[CoopAppearanceTest] PASS: lossless original appearance schema; components=" + count);
        enabled = false;
    }
}
