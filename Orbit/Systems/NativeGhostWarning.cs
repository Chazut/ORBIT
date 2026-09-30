using System;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace Orbit.Systems;

// A remote AI warning needs no gestures or weapon node. Keep native warning bookkeeping,
// including its later hostility rules, while leaving human encounters to the physical brain.
internal static class NativeGhostWarning
{
    internal static bool Supports(BotOwner bot, BotLogicDecision decision,
        Func<Vector3, float> humanDistanceSqr, float wakeDistanceSqr)
    {
        if (decision != BotLogicDecision.warnPlayer || humanDistanceSqr == null
            || !(wakeDistanceSqr > 0f) || bot?.Memory == null || bot.IsDead
            || bot.Memory.GoalEnemy != null || bot.Memory.IsUnderFire) return false;
        var request = bot.WarnData?.WarnPlayerRequest;
        var target = request?.playerToWarn;
        var other = target?.AIData?.BotOwner;
        if (target?.AIData?.IsAI != true || other == null || other == bot || other.IsDead
            || target.HealthController?.IsAlive != true || other.Memory == null
            || other.Memory.GoalEnemy != null || other.Memory.IsUnderFire
            || bot.BotsGroup?.BotGroupWarnData == null || other.BotsGroup == null
            || bot.BotsGroup == other.BotsGroup) return false;
        var world = Singleton<GameWorld>.Instance;
        var ownPlayer = world?.GetAlivePlayerBridgeByProfileID(bot.GetPlayer.ProfileId)?.iPlayer;
        var otherPlayer = world?.GetAlivePlayerBridgeByProfileID(target.ProfileId)?.iPlayer;
        if (ownPlayer == null || otherPlayer == null || bot.BotsGroup.IsEnemy(otherPlayer)
            || other.BotsGroup.IsEnemy(ownPlayer)) return false;
        // Never cancel a warning shot which is already being performed while awake.
        if (request.StateWarnPlayer is not (StateWarnPlayer.goTo or StateWarnPlayer.say1
            or StateWarnPlayer.wait or StateWarnPlayer.stay)) return false;
        return humanDistanceSqr(bot.Position) > wakeDistanceSqr
            && humanDistanceSqr(target.Position) > wakeDistanceSqr;
    }

    internal static void Complete(BotOwner bot)
    {
        var request = bot.WarnData.WarnPlayerRequest;
        var phase = request.StateWarnPlayer;
        // StopWarn releases the trigger subscription and the group's pending-warning entry.
        // Complete records the warning through BSG; it does not add or remove an enemy.
        if (Time.time <= request.EndTime)
        {
            request.SetWarnStarted();
            request.Complete();
        }
        bot.WarnData.StopWarn();
        NativeGhostDiagnostics.WarningCompleted(bot, request, phase);
    }

    internal static string Snapshot(BotOwner bot)
    {
        var request = bot.WarnData?.WarnPlayerRequest;
        if (request == null) return "warnTarget=none";
        var target = request.playerToWarn;
        return $"warnTarget={target?.ProfileId ?? "none"} warnAI={target?.AIData?.IsAI}"
            + $" warnPhase={request.StateWarnPlayer} warnRemaining={request.EndTime - Time.time:F1}s";
    }
}
