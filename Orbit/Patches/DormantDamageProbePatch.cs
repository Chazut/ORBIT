using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using Orbit.Navigation;
using Orbit.Systems;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

/// <summary>
/// Diagnostic: names whatever damages a dormant body. A sleeper's GameObject is inactive (no colliders,
/// no triggers), yet raid logs show scav ghosts waking on sudden 10-360 HP losses followed by bleeds,
/// with nobody within 80m and no border zone at the wake position. Every health-controller damage on a
/// dormant profile is logged with its type, attacker, weapon, zone check and the managed call stack, so
/// the next raid tells which BSG system (or mod) reaches an inactive body by reference.
/// </summary>
public class DormantDamageProbePatch : ModulePatch
{
    private const int MaxStackTraces = 40;
    private static int _stackTracesLogged;
    private static AccessTools.FieldRef<ActiveHealthController, Player> _playerField;
    private static bool _playerFieldFailed;

    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.ApplyDamage));

    [PatchPostfix]
    public static void Postfix(ActiveHealthController __instance, EBodyPart bodyPart, float damage, EFT.Ballistics.DamageInfo damageInfo, float __result)
    {
        try
        {
            var player = OwnerOf(__instance);
            if (player == null || !DormancySystem.IsDormantProfile(player.ProfileId)) return;

            var stack = Environment.StackTrace;
            // ORBIT's own simulated-fight casualties go through this entry point on purpose.
            if (stack.Contains("KillWithAttribution")) return;

            var attacker = "-";
            try
            {
                var bridge = damageInfo.Player;
                var iPlayer = bridge == null ? null : Traverse.Create(bridge).Property("iPlayer").GetValue<IPlayer>();
                attacker = iPlayer?.Profile?.Nickname ?? bridge?.ToString() ?? "-";
            }
            catch { }
            var weapon = "-";
            try { weapon = damageInfo.Weapon?.ShortName ?? "-"; } catch { }

            var pos = player.Position;
            Log.Info($"DORMANT DAMAGE: {player.Profile?.Nickname} took {damage:F0} ({__result:F0} applied) to {bodyPart}, " +
                     $"type {damageInfo.DamageType}, from {attacker}, weapon {weapon}, hitPoint {damageInfo.HitPoint}, " +
                     $"at {pos}, inZone={DangerZones.IsInside(pos)}, activeSelf={player.gameObject.activeSelf}");
            if (_stackTracesLogged++ < MaxStackTraces)
                Log.Info($"DORMANT DAMAGE stack:\n{stack}");
        }
        catch
        {
            // Diagnostics must never touch the damage pipeline.
        }
    }

    private static Player OwnerOf(ActiveHealthController controller)
    {
        if (!_playerFieldFailed)
        {
            try
            {
                _playerField ??= AccessTools.FieldRefAccess<ActiveHealthController, Player>("Player");
                var owner = _playerField(controller);
                if (owner != null) return owner;
            }
            catch
            {
                _playerFieldFailed = true;
            }
        }

        // Field name drifted: match the controller against the live player list instead.
        var gameWorld = Singleton<GameWorld>.Instance;
        var players = gameWorld?.AllAlivePlayersList;
        if (players == null) return null;
        for (var i = 0; i < players.Count; i++)
        {
            if (players[i]?.ActiveHealthController == controller) return players[i];
        }
        return null;
    }
}
