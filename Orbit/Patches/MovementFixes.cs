using System.Diagnostics;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Orbit.Core;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Orbit.Patches;

/// <summary>
/// Logs the small "soft" rescue teleport BSG fires via BotMover.Teleport.
/// Strictly for diagnosing pathing pathologies — production builds noop
/// this via Log.Debug being [Conditional("DEBUG")].
/// </summary>
public class SoftTeleportTracePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BotMover).GetMethod(nameof(BotMover.Teleport));
    }

    [PatchPrefix]
    public static void Patch(BotMover __instance, Vector3 rPosition)
    {
        if (__instance is GClass493) return;

        var botPosition = __instance.BotOwner_0.GetPlayer.Position;
        var sqrDist = (botPosition - rPosition).sqrMagnitude;
        if (sqrDist < 4f) return;

        var id = __instance.BotOwner_0.Id;
        var role = __instance.BotOwner_0.GetPlayer.Profile?.Info?.Settings?.Role;
        var nickName = __instance.BotOwner_0.GetPlayer.Profile?.Nickname;

        Log.Debug($"BotMover.Teleport (soft) id={id} role={role} name={nickName} pos={botPosition} target={rPosition} dist={Mathf.Sqrt(sqrDist):F1}");
        Log.Debug($"  CurTime={Time.time} LastGoodCastPointTime={__instance.LastGoodCastPointTime} PrevPosLinkedTime_1={__instance.PrevPosLinkedTime_1}");
        Log.Debug($"  PositionOnWayInner={__instance.PositionOnWayInner} dist={Vector3.Distance(botPosition, __instance.PositionOnWayInner)}");
        Log.Debug($"  PositionOnWayCasted={__instance.PositionOnWayCasted} dist={Vector3.Distance(botPosition, __instance.PositionOnWayCasted)}");
        Log.Debug($"  PrevLinkPos={__instance.PrevLinkPos} dist={Vector3.Distance(botPosition, __instance.PrevLinkPos)}");
        Log.Debug($"  PrevSuccessLinkedFrom_1={__instance.PrevSuccessLinkedFrom_1} dist={Vector3.Distance(botPosition, __instance.PrevSuccessLinkedFrom_1)}");
        Log.Debug($"  LastGoodCastPoint={__instance.LastGoodCastPoint} dist={Vector3.Distance(botPosition, __instance.LastGoodCastPoint)}");
        Log.Debug($"  trace:\n{new StackTrace(true)}");
    }
}

/// <summary>
/// Logs the "hard" rescue teleport BSG fires via BotMover.method_10 — the
/// large recovery snap fired when a bot is hopelessly stuck. Paired with
/// <see cref="RescueInterceptPatch"/> which actually reacts to it.
/// </summary>
public class HardTeleportTracePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BotMover).GetMethod(nameof(BotMover.method_10));
    }

    [PatchPrefix]
    public static void Patch(BotMover __instance, Vector3 posiblePos)
    {
        if (__instance is GClass493) return;

        var botPosition = __instance.BotOwner_0.GetPlayer.Position;
        var sqrDist = (botPosition - posiblePos).sqrMagnitude;
        if (sqrDist < 4f) return;

        var id = __instance.BotOwner_0.Id;
        var role = __instance.BotOwner_0.GetPlayer.Profile?.Info?.Settings?.Role;
        var nickName = __instance.BotOwner_0.GetPlayer.Profile?.Nickname;

        Log.Debug($"BotMover.method_10 (hard) id={id} role={role} name={nickName} pos={botPosition} target={posiblePos} dist={Mathf.Sqrt(sqrDist):F1}");
        Log.Debug($"  CurTime={Time.time} LastGoodCastPointTime={__instance.LastGoodCastPointTime} PrevPosLinkedTime_1={__instance.PrevPosLinkedTime_1}");
        Log.Debug($"  PositionOnWayInner={__instance.PositionOnWayInner} dist={Vector3.Distance(botPosition, __instance.PositionOnWayInner)}");
        Log.Debug($"  PositionOnWayCasted={__instance.PositionOnWayCasted} dist={Vector3.Distance(botPosition, __instance.PositionOnWayCasted)}");
        Log.Debug($"  PrevLinkPos={__instance.PrevLinkPos} dist={Vector3.Distance(botPosition, __instance.PrevLinkPos)}");
        Log.Debug($"  PrevSuccessLinkedFrom_1={__instance.PrevSuccessLinkedFrom_1} dist={Vector3.Distance(botPosition, __instance.PrevSuccessLinkedFrom_1)}");
        Log.Debug($"  LastGoodCastPoint={__instance.LastGoodCastPoint} dist={Vector3.Distance(botPosition, __instance.LastGoodCastPoint)}");
        Log.Debug($"  trace:\n{new StackTrace(true)}");
    }
}

/// <summary>
/// Forces MovementContext.IsAI to return false so the movement system runs
/// the human-control code path even for our bots. Without this BSG's AI
/// short-circuits much of the smoothing pipeline. Borrowed approach from
/// Solarint's SAIN.
/// </summary>
public class MovementContextHumanizePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.PropertyGetter(typeof(MovementContext), nameof(MovementContext.IsAI));
    }

    [PatchPrefix]
    public static bool Patch(ref bool __result)
    {
        __result = false;
        return false;
    }
}

/// <summary>
/// Skips BSG's BotMover.ManualFixedUpdate for bots we control. Without this
/// the vanilla mover keeps issuing path corrections that fight our own
/// movement system, producing the characteristic AI jitter when bots are
/// also in SAIN combat.
/// </summary>
public class ManualFixedUpdateSkipPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BotMover).GetMethod(nameof(BotMover.ManualFixedUpdate));
    }

    [PatchPrefix]
    public static bool PatchPrefix(BotMover __instance)
    {
        return Singleton<BotRoster>.Instance == null || !Singleton<BotRoster>.Instance.IsOrbitActive(__instance.BotOwner_0);
    }
}

/// <summary>
/// Enables the human vaulting component for bots so they can actually
/// climb low obstacles instead of getting stuck on every windowsill.
/// Skips bots running the simplified skeleton (offscreen LOD bots have no
/// vaulting rig — the component would null-deref).
/// </summary>
public class BotVaultingPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player).GetMethod(nameof(Player.InitVaultingComponent));
    }

    [PatchPrefix]
    public static void Patch(Player __instance, ref bool aiControlled)
    {
        if (__instance.UsedSimplifiedSkeleton)
        {
            return;
        }

        aiControlled = false;
    }
}
