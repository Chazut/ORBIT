using System;
using System.Reflection;
using UnityEngine.SceneManagement;

namespace Orbit.Helpers;

/// <summary>
/// Per-raid detection of map reworks that keep BSG's location id (LennoxP90's Interchange Rework, Manimal's
/// Interchange expansion and Lighthouse 1.0 backports all do): the raid loads under the vanilla id, so zones
/// and renders keyed on the id alone would serve the wrong layout. The variant is a short suffix appended to
/// the id as "{id}@{variant}" for zone files, server zone keys and editor map renders; every lookup falls
/// back to the plain id when no variant-specific entry exists, so an older server mod or zone pack keeps
/// working. Both Interchange backports rebuild the same retail layout, so they share the "rework" key.
/// Every signal is read only for the map it belongs to: a choice or preset a plugin left behind after an
/// earlier raid can never tag another map.
/// </summary>
public static class MapVariants
{
    public const string Rework = "rework";

    // BSG location ids (GameWorld.LocationId) of the maps that have a rework today.
    private const string InterchangeId = "Interchange";
    private const string LighthouseId = "Lighthouse";

    // InterchangeRework 1.1.0+ publishes an extension API meant to be read by reflection
    // (InterchangeRework.Shared.ExtensionApiContract): IsRework is true once the per-raid choice landed on the
    // rework tile. Checked first; the scene suffix below stays the ground truth for 1.0.0 and the fallback.
    private const string InterchangeReworkApiType = "InterchangeRework.Client.Api.Interchange";

    // InterchangeRework.Shared.SceneNames.IrSuffix: every swapped scene loads under its vanilla name + "_IR".
    private const string InterchangeReworkSceneSuffix = "_IR";

    // Manimal's backports own the scene preset while their replacement is loaded and drop it when the scenes
    // unload. The Lighthouse loader exposes HasReplacement; the Interchange one only keeps the cloned preset
    // in a private field.
    private const string ManimalInterchangeLoader = "Manimal.Interchange.Client.SceneLoader";
    private const string ManimalLighthouseLoader = "Manimal.Lighthouse.Client.LighthouseSceneLoader";

    /// <summary>"" for the vanilla layout, otherwise the variant suffix. Reads only loaded scene names and
    /// the rework plugins' own flags, so it is safe to call as soon as the location scenes are in.</summary>
    public static string Detect(string mapId)
    {
        if (IsMap(mapId, InterchangeId))
        {
            if (InterchangeReworkApiSaysRework()) return Rework;
            if (AnySceneEndsWith(InterchangeReworkSceneSuffix)) return Rework;
            if (ManimalLoaderActive(ManimalInterchangeLoader)) return Rework;
        }
        else if (IsMap(mapId, LighthouseId))
        {
            if (ManimalLoaderActive(ManimalLighthouseLoader)) return Rework;
        }

        return "";
    }


    // Plain reflection on purpose: HarmonyX's AccessTools logs a Warning for every miss, and a miss is the
    // normal case whenever one of the rework plugins is not installed.
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private static Type FindType(string fullName)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (var i = 0; i < assemblies.Length; i++)
        {
            try
            {
                var type = assemblies[i].GetType(fullName, false);
                if (type != null) return type;
            }
            catch
            {
                // Dynamic or broken assembly: skip it.
            }
        }
        return null;
    }

    private static bool IsMap(string mapId, string id)
        => string.Equals(mapId, id, StringComparison.OrdinalIgnoreCase);

    private static bool AnySceneEndsWith(string suffix)
    {
        try
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var name = SceneManager.GetSceneAt(i).name;
                if (name != null && name.EndsWith(suffix, StringComparison.Ordinal)) return true;
            }
        }
        catch
        {
            // Scene enumeration is best effort.
        }
        return false;
    }

    /// <summary>True when LennoxP90's extension API is present and reports the rework variant for this raid.</summary>
    private static bool InterchangeReworkApiSaysRework()
    {
        try
        {
            var api = FindType(InterchangeReworkApiType);
            if (api == null) return false;
            var flag = api.GetProperty("IsRework", Members);
            return flag != null && flag.GetValue(null) is true;
        }
        catch
        {
            // Plugin absent or reshaped: the scene names decide.
            return false;
        }
    }

    /// <summary>True when one of Manimal's scene loaders currently owns the preset (its replacement is loaded).</summary>
    private static bool ManimalLoaderActive(string typeName)
    {
        try
        {
            var loader = FindType(typeName);
            if (loader == null) return false;

            var flag = loader.GetProperty("HasReplacement", Members);
            if (flag != null) return flag.GetValue(null) is true;

            var owned = loader.GetField("_ownedPreset", Members);
            if (owned != null)
            {
                var value = owned.GetValue(null);
                return value is UnityEngine.Object unityObject ? unityObject != null : value != null;
            }
        }
        catch
        {
            // Plugin absent or reshaped: vanilla it is.
        }
        return false;
    }

    /// <summary>The key zones, geometry and renders are looked up under: the id itself for vanilla, "{id}@{variant}" otherwise.</summary>
    public static string ZoneKey(string mapId, string variant)
        => string.IsNullOrEmpty(variant) ? mapId : $"{mapId}@{variant}";
}
