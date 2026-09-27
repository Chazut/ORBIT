using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Systems;
using UnityEngine;

namespace Orbit.Tasks;

/// <summary>A single, bounded collection of nearby valuables after a loot-value departure decision.</summary>
internal sealed class LootExtractSweep
{
    internal const float Radius = 10f;
    internal const float FloorTolerance = 1.5f;
    internal const float MaxPathLength = 15f;
    internal const float MinimumValue = 50000f;
    internal const float Duration = 45f;
    private const int MaxTargets = 6;

    internal readonly Agent Owner;
    internal readonly Squad Squad;
    internal readonly Vector3 Center;
    internal readonly HashSet<int> Attempted = new();
    private readonly bool _squadDeparture;
    private readonly string _reason;
    private readonly float _deadline;
    private Waypoint _target;

    private LootExtractSweep(Agent owner, bool squadDeparture)
    {
        Owner = owner;
        Squad = owner.Squad;
        Center = owner.Position;
        _squadDeparture = squadDeparture;
        _reason = squadDeparture ? Squad.ExtractRequestedReason : owner.SoloExtractReason;
        _deadline = Time.time + Duration;
    }

    internal static bool Begin(Agent owner, WaypointSystem waypoints, bool squadDeparture)
    {
        if (owner.Squad == null || owner.Squad.LootExtractSweep != null || owner.LootExtractSweep != null)
            return false;
        var sweep = new LootExtractSweep(owner, squadDeparture);
        owner.LootExtractSweep = sweep;
        if (squadDeparture) owner.Squad.LootExtractSweep = sweep;
        if (!sweep.Next(waypoints)) return false;
        Log.Info($"LOOT EXIT SWEEP: {owner} collecting nearby valuables before {sweep._reason}, radius={Radius:F0}m budget={Duration:F0}s");
        return true;
    }

    private string StopReason()
    {
        if (Time.time >= _deadline) return "time limit";
        if (Owner.Squad != Squad || !Squad.Members.Contains(Owner)) return "owner left squad";
        if (_squadDeparture
            ? !Squad.ExtractRequested || Squad.ExtractRequestedReason != _reason
            : !Owner.SoloExtractRequested || Owner.SoloExtractReason != _reason || Squad.ExtractRequested)
            return "departure changed";
        if ((Owner.Position - Center).sqrMagnitude > Radius * Radius) return "left collection area";
        if (Time.time < Squad.GhostFightUntil || Squad.CombatCallerMemberIdx >= 0) return "combat";
        for (var i = 0; i < Squad.Members.Count; i++)
        {
            var member = Squad.Members[i];
            if (member == null || !member.IsActive || member.SoloExtractIsEmergency
                || member.Bot?.Memory is { HaveEnemy: true } or { IsUnderFire: true })
                return "combat or emergency";
        }
        return null;
    }

    internal bool Maintain(WaypointSystem waypoints)
    {
        var reason = StopReason();
        if (reason == null && (_target == null || Owner.Objective.Location != _target
            || Owner.Objective.Status is ObjectiveStatus.Failed or ObjectiveStatus.Finished))
            reason = "collection interrupted";
        if (reason == null) return true;
        End(waypoints, reason);
        return false;
    }

    internal bool Next(WaypointSystem waypoints)
    {
        var reason = StopReason();
        if (reason != null || Attempted.Count >= MaxTargets)
        {
            End(waypoints, reason ?? "target limit");
            return false;
        }
        var next = waypoints.FindNearbyExtractLoot(this);
        if (next == null || !waypoints.TryClaim(next.Id, Owner.Id))
        {
            End(waypoints, "no accessible valuable with inventory space");
            return false;
        }
        if (_target != null) waypoints.ReleaseClaim(_target.Id, Owner.Id);
        Attempted.Add(next.Id);
        _target = next;
        Owner.Objective.Location = next;
        Owner.Objective.SplinterParent = null;
        Owner.Objective.ArrivalPath = null;
        Owner.Objective.Status = ObjectiveStatus.None;
        Owner.Objective.DispatchTime = Time.time;
        Owner.Guard.CoverPoint = null;
        if (_squadDeparture)
        {
            Squad.Objective.LocationPrevious = Squad.Objective.Location;
            Squad.Objective.Location = next;
            Squad.PreInterruptObjectiveLocation = null;
            Squad.Objective.Status = SquadObjectiveState.Active;
            Squad.Objective.StartTime = Time.time;
            Squad.Objective.Duration = _deadline - Time.time;
            Squad.Objective.DurationAdjusted = false;
            Squad.Objective.CoverPoints.Clear();
        }
        Log.Debug($"LOOT EXIT SWEEP: {Owner} target={next} attempt={Attempted.Count}/{MaxTargets} left={_deadline - Time.time:F1}s");
        return true;
    }

    private void End(WaypointSystem waypoints, string reason)
    {
        if (Owner.LootExtractSweep == this) Owner.LootExtractSweep = null;
        if (Squad.LootExtractSweep == this) Squad.LootExtractSweep = null;
        if (_target != null)
        {
            waypoints.ReleaseClaim(_target.Id, Owner.Id);
            if (Owner.Objective.Location == _target)
            {
                if (Owner.LootHandler?.LootTaskRunning == true) Owner.LootHandler.Cancel();
                Owner.Objective.Status = ObjectiveStatus.Finished;
            }
            Log.Info($"LOOT EXIT SWEEP: {Owner} finished ({reason}); departure resumes");
        }
    }
}
