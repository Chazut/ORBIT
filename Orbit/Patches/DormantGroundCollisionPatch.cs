using System.Reflection;
using EFT;
using HarmonyLib;
using Orbit.Systems;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

/// <summary>
/// GameWorld ticks every player's MovementContext whether or not its GameObject is active, so
/// UpdateGroundCollision and CheckFlying keep running on sleeping bodies. The ground raycast misses now
/// and then while the ghost walk drags the inactive transform along the navmesh, the context then books
/// every metre of downhill walking as freefall and HandleFall lands real leg damage on "landing" (up to
/// 140 HP per leg in the probe logs: type Fall, no attacker, activeSelf=false), which tripped the damage
/// wake and left bots limping and bleeding. Dormant bodies skip the ground pass entirely, no raycast and
/// no fall bookkeeping; the wake path calls ResetFlying so the first live tick starts from the current
/// height instead of the sleep altitude.
/// </summary>
public class DormantGroundCollisionPatch : ModulePatch
{
    private static AccessTools.FieldRef<MovementContext, Player> _playerField;
    private static bool _playerFieldFailed;

    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(MovementContext), "UpdateGroundCollision", [typeof(float)]);

    [PatchPrefix]
    public static bool Prefix(MovementContext __instance)
    {
        if (_playerFieldFailed) return true;
        try
        {
            _playerField ??= AccessTools.FieldRefAccess<MovementContext, Player>("_player");
            var player = _playerField(__instance);
            return player == null || !DormancySystem.IsDormantProfile(player.ProfileId);
        }
        catch
        {
            // Field renamed by a game update: fall back to vanilla behaviour rather than break every tick.
            _playerFieldFailed = true;
            return true;
        }
    }
}
