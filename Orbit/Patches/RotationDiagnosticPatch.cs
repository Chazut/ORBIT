using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Orbit.Core;
using Orbit.Helpers;
using Orbit.Systems;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Orbit.Patches;

// Both hooks are observers. They never skip the original or write engine state.
public sealed class RotationRequestDiagnosticPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.Rotate),
        new[] { typeof(Vector2), typeof(bool) });

    [PatchPrefix]
    [HarmonyPriority(Priority.First)]
    public static void Prefix(Player __instance, Vector2 deltaRotation, out string __state)
        => __state = RotationDiagnostics.Before(__instance, deltaRotation);

    [PatchPostfix]
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(Player __instance, string __state, bool __runOriginal)
        => RotationDiagnostics.After(__instance, __state, __runOriginal);
}

public sealed class RotationStateDiagnosticPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotOwner), nameof(BotOwner.UpdateManual));

    [PatchPostfix]
    public static void Postfix(BotOwner __instance) => RotationDiagnostics.Report(__instance);
}

internal static class RotationDiagnostics
{
    private sealed class Observation
    {
        internal float Time = -1f;
        internal Vector2 Requested, Before, After;
        internal bool Original;
    }

    private static readonly RotationProbeWindow Gate = new();
    private static readonly Dictionary<string, Observation> Samples = new();
    private static readonly HashSet<MethodBase> Methods = new();
    private static readonly FieldInfo RawRotation = AccessTools.Field(typeof(MovementContext), "_rotation");
    private static readonly FieldInfo BlockSteering = AccessTools.Field(typeof(BotSteering), "_blockSteering");
    private static bool _error, _owners;

    internal static void Clear()
    {
        Gate.Clear();
        Samples.Clear();
        Methods.Clear();
        _error = _owners = false;
    }

    internal static string Before(Player player, Vector2 delta)
    {
        if (!Log.DebugEnabled) return null;
        try
        {
            if (player?.AIData?.BotOwner == null || player.MovementContext == null) return null;
            var id = player.ProfileId;
            if (!Gate.Sample(Gate.Get(id), Time.time)) return null;
            if (!Samples.TryGetValue(id, out var sample)) Samples[id] = sample = new Observation();
            sample.Time = Time.time;
            sample.Requested = delta;
            sample.Before = player.Rotation;
            return id;
        }
        catch (Exception e) { Error(e); return null; }
    }

    internal static void After(Player player, string id, bool original)
    {
        if (id == null) return;
        try
        {
            if (!Samples.TryGetValue(id, out var sample)) return;
            sample.After = player.Rotation;
            sample.Original = original;
        }
        catch (Exception e) { Error(e); }
    }

    internal static void Report(BotOwner bot)
    {
        if (!Log.DebugEnabled) return;
        try
        {
            var player = bot?.GetPlayer;
            if (player?.MovementContext == null || !player.HealthController.IsAlive) return;
            if (DormancySystem.IsDormantProfile(player.ProfileId)) return;
            var window = Gate.Get(player.ProfileId);
            if (!Gate.Report(window, Time.time, Time.frameCount)) return;
            Samples.TryGetValue(player.ProfileId, out var sample);
            var context = player.MovementContext;
            var raw = RawRotation?.GetValue(context);
            var block = bot.Steering == null ? null : BlockSteering?.GetValue(bot.Steering);
            var layer = bot.Brain?.BaseBrain?.CurLayerInfo?.Name();
            var orbit = Singleton<BotRoster>.Instance?.IsOrbitActive(bot) ?? false;
            var age = sample == null ? -1f : Time.time - sample.Time;
            Log.Debug($"ROTATION PROBE: id={player.ProfileId} name={player.Profile.Nickname} role={player.Profile.Info.Settings.Role} layer={layer} orbit={orbit} state={player.CurrentManagedState?.GetType().FullName} rotation={player.Rotation} raw={raw} yaw={player.Transform.rotation.eulerAngles.y:F1} look={player.LookDirection} spirit={player.Spirit?.IsActive} steering={bot.Steering?.GetType().Name}/{bot.Steering?.SteeringMode} speed={bot.Steering?.Speed} blocked={block} target={bot.Steering?.LookDirection} paused={bot.Mover?.Pause} moving={bot.Mover?.IsMoving} mover={bot.Mover?.CurrentState} requestAge={age:F1} request={sample?.Requested} before={sample?.Before} after={sample?.After} original={sample?.Original}");
            if (!_owners)
            {
                _owners = true;
                Owners(AccessTools.Method(typeof(Player), nameof(Player.Rotate)));
                Owners(AccessTools.PropertySetter(typeof(MovementContext), nameof(MovementContext.Rotation)));
                Owners(AccessTools.Method(typeof(BotSteering), "Steering"));
                Owners(AccessTools.Method(typeof(BotSteering), "ManualFixedUpdate"));
                Owners(AccessTools.Method(typeof(BotOwner), nameof(BotOwner.UpdateManual)));
            }
            if (player.CurrentManagedState != null)
                Owners(AccessTools.Method(player.CurrentManagedState.GetType(), "Rotate", new[] { typeof(Vector2), typeof(bool) }));
        }
        catch (Exception e) { Error(e); }
    }

    private static void Owners(MethodBase method)
    {
        if (method == null || Methods.Count >= 32 || !Methods.Add(method)) return;
        var info = Harmony.GetPatchInfo(method);
        var hooks = info == null ? "none" : string.Join(";", info.Prefixes.Select(p => $"pre:{p.owner}/{p.PatchMethod.DeclaringType?.FullName}.{p.PatchMethod.Name}")
            .Concat(info.Postfixes.Select(p => $"post:{p.owner}/{p.PatchMethod.DeclaringType?.FullName}.{p.PatchMethod.Name}"))
            .Concat(info.Transpilers.Select(p => $"il:{p.owner}/{p.PatchMethod.DeclaringType?.FullName}.{p.PatchMethod.Name}")));
        Log.Debug($"ROTATION PATCHES: method={method.DeclaringType?.FullName}.{method.Name} hooks={hooks}");
    }

    private static void Error(Exception error)
    {
        if (_error) return;
        _error = true;
        Log.Debug($"ROTATION PROBE ERROR: {error.GetType().Name}: {error.Message}");
    }
}
