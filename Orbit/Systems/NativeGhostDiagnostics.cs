using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace Orbit.Systems;

/// <summary>Bounded refusal snapshots, including bots which have never entered Ghost.</summary>
internal static class NativeGhostDiagnostics
{
    private static readonly Dictionary<string, float> NextReport = new();
    public static void Clear() => NextReport.Clear();
    public static void Forget(BotOwner bot) { if (bot?.ProfileId != null) NextReport.Remove(bot.ProfileId); }

    public static bool Refuse(BotOwner bot, string reason, float humanDistance = -1f, int groupSize = 1)
    {
        if (bot?.ProfileId == null) return false;
        if (NextReport.TryGetValue(bot.ProfileId, out var next) && Time.time < next) return false;
        NextReport[bot.ProfileId] = Time.time + 60f;
        try
        {
            var enemy = bot.Memory?.GoalEnemy;
            var distance = enemy?.Person == null ? -1f : Vector3.Distance(bot.Position, enemy.Person.Position);
            var seen = enemy == null ? -1f : Time.time - enemy.PersonalLastSeenTime;
            Log.Info($"NATIVE GHOST REFUSED: {bot.Profile?.Nickname} [{bot.ProfileId}] reason={reason} role={bot.Profile?.Info?.Settings?.Role}"
                + $" layer={NativeGhostPartisan.Layer(bot) ?? "none"} decision={bot.Brain?.LastDecision} state={bot.BotState} bodyActive={bot.gameObject.activeSelf}"
                + $" human={humanDistance:F1}m group={groupSize} enemy={enemy != null} enemyId={enemy?.Person?.ProfileId ?? "none"} enemyDistance={distance:F1}m visible={enemy?.IsVisible} canShoot={enemy?.CanShoot}"
                + $" seenAgo={seen:F1}s underFire={bot.Memory?.IsUnderFire} path={bot.Mover?.ActualPathController?.HavePath} position={bot.Position}"
                + " " + NativeGhostPatrol.Snapshot(bot));
        }
        catch (Exception e)
        {
            Log.Info($"NATIVE GHOST REFUSED: [{bot.ProfileId}] reason={reason} snapshot-error={e.GetType().Name}");
        }
        return false;
    }
}
