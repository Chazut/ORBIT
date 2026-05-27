using System;
using System.Reflection;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Orbit.UI;

/// <summary>
/// Suffixes EFT's bottom-left version label with " | ORBIT &lt;version&gt;" so
/// players can see at a glance the mod is loaded. Mirrors SAIN's version-
/// label patch — patching PreloaderUI.method_6 in PREFIX and writing the
/// suffix into both the local version string AND the LocalizedText's
/// LocalizationKey.
///
/// The LocalizationKey side is essential: EFT's UI re-reads it any time the
/// language changes or the label re-renders, so writing only .text gets
/// overwritten. Setting LocalizationKey makes the suffix sticky.
///
/// ___string_2 + ____alphaVersionLabel are Harmony field-injection names
/// (triple underscore + field name; quadruple because EFT's field itself is
/// named _alphaVersionLabel).
/// </summary>
public class VersionLabelPatch : ModulePatch
{
    private const string ModName = "ORBIT";

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(PreloaderUI), nameof(PreloaderUI.method_6));
    }

    [PatchPrefix]
    public static void Prefix(ref string ___string_2, ref LocalizedText ____alphaVersionLabel)
    {
        if (___string_2.IndexOf(ModName, StringComparison.OrdinalIgnoreCase) >= 0) return;

        ___string_2 += $" | {ModName} {Plugin.OrbitVersion}";
        ____alphaVersionLabel.LocalizationKey = ___string_2;
    }
}
