using System;
using System.Collections.Generic;
using EFT;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

public sealed partial class NativeGhostSystem
{
    private sealed class HearingDetour
    {
        internal NativeGhostNavigation Previous;
        internal Vector3 Target;
        internal float ExpiresAt;
        internal float ArrivedAt = -1f;
        internal Vector3? ReturnPosition;
        internal bool Returning;
    }

    private static readonly Dictionary<BotOwner, Vector3> HearingWakeGoals = new();

    internal bool IsInvestigating(BotOwner bot)
        => bot != null && Sleepers.TryGetValue(bot, out var state) && state.Hearing != null;

    internal bool CanInvestigate(BotOwner bot)
        => OwnsInactiveMovement(bot) && !InFight(bot) && !IsInvestigating(bot)
            && !Sleepers[bot].Doors.Pending
            && !CombatRequiresBody(bot) && !NeedsBody(bot)
            && !(bot.Mover.Pause && bot.Mover.RemainPause > 0f);

    internal bool Investigate(IReadOnlyList<BotOwner> group, Vector3 source)
    {
        if (group == null || group.Count == 0 || !NativeGhostOrders.Valid(source)) return false;
        for (var i = 0; i < group.Count; i++)
            if (!CanInvestigate(group[i]) || GhostHearingPolicy.Category(group[i]) == GhostHearingCategory.None) return false;
        if (!NativeGhostNavigation.TakeQuery() || !NavMesh.SamplePosition(source, out var hit, 6f, NavMesh.AllAreas)
            || Mathf.Abs(hit.position.y - source.y) > 2.5f || DangerZones.IsInside(hit.position)) return false;
        var path = new NavMeshPath();
        if (!NavMesh.CalculatePath(group[0].Position, hit.position, NavMesh.AllAreas, path)
            || path.status != NavMeshPathStatus.PathComplete || path.corners.Length < 2) return false;

        try
        {
            for (var i = 0; i < group.Count; i++)
            {
                var bot = group[i];
                var state = Sleepers[bot];
                state.Navigation.RestorePathReach();
                var wasHolding = !state.Navigation.HasOrder;
                if (!state.Navigation.HasOrder) state.Navigation.Queue(bot.Position, 0.5f);
                state.Hearing = new HearingDetour
                {
                    Previous = state.Navigation, Target = hit.position,
                    ReturnPosition = wasHolding ? bot.Position : null,
                    ExpiresAt = Time.time + Mathf.Clamp(Vector3.Distance(bot.Position, hit.position) / WalkSpeed + 45f, 60f, 300f),
                };
                state.Navigation = new NativeGhostNavigation(bot, _doors);
                bot.Mover.ActualPathController.Stop();
                // Use the validated leader route; followers calculate from their own positions.
                if (i == 0) state.Navigation.RequestWay(hit.position, path.corners, 3f, "ghost-hearing");
                else state.Navigation.Request(hit.position, 3f);
                state.Adapter?.SuspendProgress();
            }
            return true;
        }
        catch (Exception e)
        {
            for (var i = 0; i < group.Count; i++)
                if (Sleepers.TryGetValue(group[i], out var state)) EndHearing(state, "start failed", true);
            Log.Warning($"GHOST HEARING: native investigation unavailable: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    private static void EndHearing(Sleeper state, string reason, bool resume)
    {
        if (state.Hearing == null) return;
        var previous = state.Hearing.Previous;
        var detour = state.Navigation;
        state.Hearing = null;
        state.Navigation = previous;
        try
        {
            detour.Cancel();
            state.Bot.Mover.ActualPathController.Stop();
            state.Bot.Mover.IsMoving = false;
            previous.Suspend();
            if (resume)
            {
                previous.Update();
                state.Adapter?.ReissueOrder();
            }
        }
        catch (Exception e)
        {
            RequestWake(state, $"hearing route restore failed: {e.GetType().Name}: {e.Message}");
        }
        Log.Info($"GHOST HEARING: {state.Bot.Profile.Nickname} native investigation ended: {reason}; original behaviour resumes");
    }

    private static void UpdateHearing(Sleeper state)
    {
        var hearing = state.Hearing;
        if (hearing == null) return;
        if (GhostHearingPolicy.Category(state.Bot) == GhostHearingCategory.None)
        { EndHearing(state, "assignment changed", true); return; }
        if (Time.time >= hearing.ExpiresAt || state.Navigation.Failed)
        { EndHearing(state, state.Navigation.Failed ? "route failed" : "timeout", true); return; }
        if (Vector3.Distance(state.Bot.Position, hearing.Target) > (hearing.Returning ? 0.5f : 3f)) return;
        if (hearing.Returning) { EndHearing(state, "returned to post", true); return; }
        if (hearing.ArrivedAt < 0f) hearing.ArrivedAt = Time.time;
        if (Time.time - hearing.ArrivedAt < 8f) return;
        if (hearing.ReturnPosition.HasValue)
        {
            hearing.Returning = true;
            hearing.Target = hearing.ReturnPosition.Value;
            hearing.ExpiresAt = Time.time + Mathf.Clamp(Vector3.Distance(state.Bot.Position, hearing.Target) / WalkSpeed + 45f, 60f, 300f);
            state.Navigation.Request(hearing.Target, 0.5f);
        }
        else EndHearing(state, "search complete", true);
    }

    private static void PrepareHearingWake(Sleeper state)
    {
        if (state.Hearing == null) return;
        var goal = state.Hearing.Previous.Target;
        EndHearing(state, "wake", false);
        if (goal.HasValue && !state.Bot.IsDead) HearingWakeGoals[state.Bot] = goal.Value;
    }

    private static void ResumeHearingAfterWake(BotOwner bot)
    {
        if (!HearingWakeGoals.TryGetValue(bot, out var goal)) return;
        HearingWakeGoals.Remove(bot);
        if (!bot.IsDead && bot.Memory?.GoalEnemy == null && bot.Memory?.IsUnderFire != true)
            bot.Mover.GoToPoint(goal, true, 0.5f, false, false, false, false);
    }
}
