using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace Orbit.Systems;

/// <summary>Bounded refusal snapshots, including bots which have never entered Ghost.</summary>
internal static class NativeGhostDiagnostics
{
    private static readonly Dictionary<string, float> NextReport = new();
    private static readonly HashSet<string> ReportedBrainFailures = new();
    private const int MaxBrainFailureReports = 8;
    public static void Clear()
    {
        NextReport.Clear();
        ReportedBrainFailures.Clear();
    }
    public static void Forget(BotOwner bot) { if (bot?.ProfileId != null) NextReport.Remove(bot.ProfileId); }

    internal static void BrainFailure(BotOwner bot, string decision, Exception exception)
    {
        // The finalizer contains this exception, so Unity never prints its stack.
        // Keep one full trace per bot, capped across the raid, with no work on healthy ticks.
        if (bot?.ProfileId == null || ReportedBrainFailures.Count >= MaxBrainFailureReports
            || !ReportedBrainFailures.Add(bot.ProfileId)) return;
        Log.Warning($"NATIVE GHOST BRAIN FAILURE: {bot.Profile?.Nickname} [{bot.ProfileId}] decision={decision}\n{exception}");
        try
        {
            var leader = bot.BotFollower?.BossToFollow;
            Log.Warning($"NATIVE GHOST BRAIN CONTEXT: [{bot.ProfileId}] layer={NativeGhostPartisan.Layer(bot) ?? "none"}"
                + $" state={bot.BotState} position={bot.Position} leaderPresent={leader != null} leaderAlive={leader?.IsAlive}"
                + $" follower={bot.BotFollower?.PatrolDataFollower?.followerAIBase?.GetType().Name ?? "none"}");
        }
        catch (Exception e)
        {
            Log.Warning($"NATIVE GHOST BRAIN CONTEXT: [{bot.ProfileId}] unavailable ({e.GetType().Name})");
        }
    }

    public static bool Refuse(BotOwner bot, string reason, float humanDistance = -1f, int groupSize = 1)
    {
        if (bot?.ProfileId == null) return false;
        NativeMedicalDiagnostics.Refused(bot, reason);
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
                + " " + NativeGhostPatrol.Snapshot(bot)
                + (bot.Profile?.Info?.Settings?.Role == WildSpawnType.marksman
                    ? $" cover={bot.Memory?.IsInCover} prone={bot.GetPlayer?.MovementContext?.IsInPronePose} pose={bot.GetPlayer?.PoseLevel}"
                    : ""));
        }
        catch (Exception e)
        {
            Log.Info($"NATIVE GHOST REFUSED: [{bot.ProfileId}] reason={reason} snapshot-error={e.GetType().Name}");
        }
        return false;
    }
}
