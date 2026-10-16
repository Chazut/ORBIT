using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using EFT;
using HarmonyLib;
using Orbit.Navigation;
using Orbit.Systems;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Helpers;

// Capture only early casualties, before death subscribers dispose their brain or body.
// No damage or death operation is replaced and no stack is captured during ordinary ticks.
internal static class SpawnDeathDiagnostics
{
    private sealed class Entry
    {
        internal BotOwner Bot;
        internal Vector3 Position;
        internal float Prepared, Active = -1f;
        internal bool Reported;
    }
    private static readonly Dictionary<Player, Entry> Entries = new();
    private static readonly FieldInfo Aggressor = AccessTools.Field(typeof(Player), "LastAggressor");
    internal static void Clear() => Entries.Clear();

    internal static void Register(BotOwner bot, bool active = false)
    {
        var player = bot?.GetPlayer;
        if (player == null) return;
        if (!Entries.TryGetValue(player, out var entry))
        {
            if (Entries.Count >= 1024) return;
            Entries[player] = entry = new Entry { Bot = bot, Position = player.Position, Prepared = Time.realtimeSinceStartup };
        }
        if (active && entry.Active < 0f) entry.Active = Time.realtimeSinceStartup;
    }

    internal static void OnDeath(Player player, EDamageType damageType)
    {
        if (player == null || !Entries.TryGetValue(player, out var entry) || entry.Reported) return;
        var activeAge = entry.Active < 0 ? -1f : Time.realtimeSinceStartup - entry.Active;
        if (activeAge > 10f) return;
        entry.Reported = true;
        try
        {
            var bot = entry.Bot;
            var mesh = NavMesh.SamplePosition(player.Position, out var hit, .75f, NavMesh.AllAreas);
            var detail = FormattableString.Invariant($"damageType={damageType} activeAge={activeAge:F3}s preparedAge={Time.realtimeSinceStartup - entry.Prepared:F3}s")
                + $" spawn={entry.Position} position={player.Position} state={bot.BotState} bodyActive={bot.gameObject.activeSelf}"
                + $" ghost={DormancySystem.IsDormantProfile(player.ProfileId)} role={player.Profile?.Info?.Settings?.Role}"
                + $" aggressor={(Aggressor?.GetValue(player) as IPlayer)?.ProfileId ?? "none"} killer={player.KillerId ?? "none"}"
                + $" grounded={player.MovementContext?.IsGrounded} dangerZone={DangerZones.IsInside(player.Position)}"
                + $" nearbyMesh={mesh} meshPoint={(mesh ? hit.position.ToString() : "none")}";
            var frames = new StackTrace(1, false).GetFrames();
            var callers = new List<string>();
            for (var i = 0; frames != null && i < frames.Length && callers.Count < 10; i++)
            {
                var method = frames[i].GetMethod();
                if (method != null) callers.Add(method.DeclaringType?.FullName + "." + method.Name);
            }
            detail += " callers=" + string.Join(" > ", callers);
            Log.Warning($"SPAWN DEATH: {player.Profile?.Nickname} [{player.ProfileId}] {detail}");
            PerformanceJournal.Event("spawn-death", player.ProfileId, detail: detail);
        }
        catch (Exception e) { Log.Warning($"SPAWN DEATH: [{player.ProfileId}] damageType={damageType} diagnostic unavailable: {e.GetType().Name}"); }
    }
}

internal sealed class SpawnDeathPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), "OnDead", new[] { typeof(EDamageType) });
    [PatchPrefix]
    private static void Prefix(Player __instance, EDamageType damageType) => SpawnDeathDiagnostics.OnDeath(__instance, damageType);
}
