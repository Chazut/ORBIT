using System;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace Orbit.Helpers;

/// <summary>
/// Per-raid detection of map reworks that keep BSG's location id (LennoxP90's Interchange Rework and
/// Manimal's Lighthouse 1.0 backport both do): the raid loads under the vanilla id, so zones and renders
/// keyed on the id alone would serve the wrong layout. The variant is a short suffix appended to the id as
/// "{id}@{variant}" for zone files, server zone keys and editor map renders; every lookup falls back to
/// the plain id when no variant-specific entry exists, so an older server mod or zone pack keeps working.
/// </summary>
public static class MapVariants
{
    public const string Rework = "rework";

    // InterchangeRework.Shared.SceneNames.IrSuffix: every swapped scene loads under its vanilla name + "_IR".
    private const string InterchangeReworkSceneSuffix = "_IR";
    // Manimal.Lighthouse.Client.LighthouseSceneLoader.HasReplacement: true while the backport owns the preset.
    private const string LighthouseLoaderType = "Manimal.Lighthouse.Client.LighthouseSceneLoader";
    private const string LighthouseLoaderFlag = "HasReplacement";

    /// <summary>"" for the vanilla layout, otherwise the variant suffix. Reads only loaded scene names and
    /// the rework plugins' own flags, so it is safe to call as soon as the location scenes are in.</summary>
    public static string Detect(string mapId)
    {
        try
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var name = SceneManager.GetSceneAt(i).name;
                if (name != null && name.EndsWith(InterchangeReworkSceneSuffix, StringComparison.Ordinal))
                    return Rework;
            }
        }
        catch
        {
            // Scene enumeration is best effort.
        }

        try
        {
            var loader = AccessTools.TypeByName(LighthouseLoaderType);
            var flag = loader == null ? null : AccessTools.Property(loader, LighthouseLoaderFlag);
            if (flag?.GetValue(null) is true) return Rework;
        }
        catch
        {
            // Plugin absent or reshaped: vanilla it is.
        }

        return "";
    }

    /// <summary>The key zones, geometry and renders are looked up under: the id itself for vanilla, "{id}@{variant}" otherwise.</summary>
    public static string ZoneKey(string mapId, string variant)
        => string.IsNullOrEmpty(variant) ? mapId : $"{mapId}@{variant}";
}
