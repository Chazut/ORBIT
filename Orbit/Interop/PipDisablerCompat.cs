using System;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using EFT;
using EFT.CameraControl;

namespace Orbit.Interop;

/// <summary>Optional, read-only access to PiP Disabler's active optic and continuous zoom.</summary>
internal static class PipDisablerCompat
{
    internal const string PluginGuid = "com.fiodor.pipdisabler";
    private static BaseUnityPlugin _plugin;
    private static Func<bool> _active, _freelook;
    private static Func<OpticSight> _optic;
    private static Func<float> _zoom;

    internal static void Initialize()
    {
        _plugin = null;
        _active = _freelook = null;
        _optic = null;
        _zoom = null;
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var info) || info.Instance == null) return;
        try
        {
            var assembly = info.Instance.GetType().Assembly;
            var lifecycle = assembly.GetType("PiPDisabler.ScopeLifecycle", true);
            var fov = assembly.GetType("PiPDisabler.FovController", true);
            var freelook = assembly.GetType("PiPDisabler.FreelookTracker", true);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            _active = (Func<bool>)lifecycle.GetMethod("ShouldSuppressVanillaPiPNow", flags)
                .CreateDelegate(typeof(Func<bool>));
            _optic = (Func<OpticSight>)lifecycle.GetProperty("ActiveOptic", flags).GetGetMethod(true)
                .CreateDelegate(typeof(Func<OpticSight>));
            _zoom = (Func<float>)fov.GetMethod("GetEffectiveMagnification", flags)
                .CreateDelegate(typeof(Func<float>));
            _freelook = (Func<bool>)freelook.GetProperty("IsFreelooking", flags).GetGetMethod(true)
                .CreateDelegate(typeof(Func<bool>));
            _plugin = info.Instance;
            Log.Always("PiP Disabler scoped wake compatibility ready");
        }
        catch (Exception e)
        {
            // An unknown optional-mod API must never prevent normal ORBIT startup or scoped wake.
            Log.Always($"PiP Disabler scoped wake compatibility unavailable: {e.GetType().Name}");
        }
    }

    internal static bool TryRead(Player player, out float zoom)
    {
        zoom = 1f;
        if (_plugin == null || !_plugin.isActiveAndEnabled || player == null || !player.IsYourPlayer) return false;
        try
        {
            if (player.HealthController is not { IsAlive: true }
                || player.HandsController is not Player.FirearmController { IsAiming: true }
                || !_active() || _freelook()) return false;
            var scope = player.ProceduralWeaponAnimation?.CurrentScope;
            var optic = _optic();
            if (scope == null || !scope.IsOptic || optic == null
                || scope.ScopePrefabCache?.CurrentModOpticSight != optic) return false;
            zoom = _zoom();
            return zoom >= 1f && zoom <= 100f;
        }
        catch
        {
            // Scope switches, weapon disposal and optional-mod changes fall back to vanilla reads.
            return false;
        }
    }
}
