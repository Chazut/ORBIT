using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Tasks.Actions;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

internal static class SquadTravel
{
    private static int _probeFrame = -1, _probes;

    private static bool Eligible(Agent agent)
        => agent.IsActive && !agent.IsDormant && agent.Squad is { Size: > 1 }
            && agent.TaskAssignment.Task is GotoObjectiveAction
            && agent.Movement.HasPath && agent.Movement.Status == MovementStatus.Moving
            && !agent.Movement.Prone && agent.Movement.Travel?.Paused != true
            && agent.Look.Target == null && !agent.SoloExtractRequested && !agent.SoloExtractIsEmergency
            && !agent.Bot.Memory.IsUnderFire && agent.Bot.Memory.GoalEnemy == null
            && !agent.Bot.Medecine.Using && !agent.Bot.DoorOpener.Interacting
            && Time.time >= agent.Movement.DoorInteractHoldUntil;

    // Changes only local steering along an existing route. No new destination, pause or speed override.
    internal static Vector3 Steer(Agent agent, Vector3 ordinary, bool doorsNearby, bool sprintRequested)
    {
        var state = agent.Movement.SquadTravel;
        if (!Eligible(agent) || doorsNearby || sprintRequested || agent.Player.Physical.Sprinting)
        { state.Suspend(); return ordinary; }

        var squad = agent.Squad;
        int slot = -1;
        bool nearbyMember = false;
        for (int i = 0; i < squad.Members.Count; i++)
        {
            var member = squad.Members[i];
            if (ReferenceEquals(member, agent)) { slot = i; continue; }
            if (member.IsActive && !member.IsDormant && member.Movement.Status == MovementStatus.Moving
                && (member.Position - agent.Position).sqrMagnitude < 900f) nearbyMember = true;
        }
        if (slot < 0 || !nearbyMember) { state.Suspend(); return ordinary; }

        var movement = agent.Movement;
        int corner = movement.CurrentCorner;
        if (corner <= 0 || corner >= movement.Path.Length) { state.Suspend(); return ordinary; }
        var start = movement.Path[corner - 1];
        var end = movement.Path[corner];
        var segment = end - start;
        float segmentSqr = segment.x * segment.x + segment.z * segment.z;
        if (segmentSqr < 1f) { state.Suspend(); return ordinary; }

        float cornerDistance = (end - agent.Position).magnitude;
        float targetDistance = (movement.Target - agent.Position).magnitude;
        state.Initialize(agent.Id, squad.Id, slot, squad.Size, Time.time);
        state.PreparedFrame = Time.frameCount;
        state.CanScan = cornerDistance > 4f && targetDistance > 8f;
        state.Forward = new Vector3(segment.x, 0, segment.z).normalized;
        if (state.Revision != movement.PathRevision || state.Corner != corner)
        {
            state.HasLane = false;
            state.Revision = movement.PathRevision; state.Corner = corner; state.NextProbe = Time.time;
        }

        float width = state.DesiredWidth(Time.time, agent.Bot.AIData.EnvironmentId != 0, cornerDistance, targetDistance);
        if (Mathf.Abs(width) < .1f)
        { state.HasLane = false; state.LaneWidth = 0; return ordinary; }

        if (_probeFrame != Time.frameCount) { _probeFrame = Time.frameCount; _probes = 0; }
        if (Time.time >= state.NextProbe && _probes < 2)
        {
            _probes++;
            state.NextProbe = Time.time + .45f + slot * .035f;
            var fromStart = agent.Position - start;
            float t = Mathf.Clamp01((fromStart.x * segment.x + fromStart.z * segment.z) / segmentSqr);
            float ahead = Mathf.Min(3.5f / Mathf.Sqrt(segmentSqr), 1f - t);
            var center = Vector3.Lerp(start, end, t + ahead);
            var side = new Vector3(state.Forward.z, 0, -state.Forward.x);
            state.HasLane = false;
            state.LaneWidth = 0;
            // A narrow corridor falls back to half width, then the original path.
            for (int attempt = 0; attempt < 2; attempt++, width *= .5f)
            {
                if (!TryLane(agent.Position, center + side * width, out var target)) continue;
                // Never turn around merely to reach an optional lateral slot.
                if (Vector3.Dot(target - agent.Position, state.Forward) < .5f) continue;
                state.LaneTarget = target; state.LaneWidth = width; state.HasLane = true;
                break;
            }
        }
        var delta = state.LaneTarget - agent.Position;
        return state.HasLane && Vector3.Dot(delta, state.Forward) > .5f ? delta.normalized : ordinary;
    }

    private static bool TryLane(Vector3 from, Vector3 candidate, out Vector3 target)
    {
        target = default;
        if (!NavMesh.SamplePosition(candidate, out var hit, .55f, NavMesh.AllAreas)
            || Mathf.Abs(hit.position.y - candidate.y) > .5f
            || NavMesh.Raycast(from, hit.position, out _, NavMesh.AllAreas)) return false;
        var delta = hit.position - from;
        int mask = LayersMaskController.HighPolyWithTerrainMask | (1 << LayersMaskController.DoorLayer);
        if (Physics.SphereCast(from + Vector3.up * .8f, .3f, delta.normalized, out _, delta.magnitude,
            mask, QueryTriggerInteraction.Ignore)) return false;
        target = hit.position;
        return true;
    }

    internal static bool TryLook(Agent agent, out Vector3 direction)
    {
        direction = default;
        var state = agent.Movement.SquadTravel;
        if (state.PreparedFrame != Time.frameCount || !Eligible(agent))
        { state.Suspend(); return false; }
        float yaw = state.WatchYaw(Time.time, state.CanScan && !agent.Player.Physical.Sprinting, out var started);
        if (started && Time.time >= state.NextReport && (Log.InfoEnabled || PerformanceJournal.Enabled))
        {
            state.NextReport = Time.time + 30f;
            var detail = $"slot={state.Slot + 1}/{state.Count} requestedYaw={state.ScanYaw:F1} lane={state.LaneWidth:F2} moving=True";
            if (Log.InfoEnabled) Log.Info($"TRAVEL WATCH: {agent} {detail}");
            PerformanceJournal.Event("travel-watch", agent.Bot.Profile.Id, detail, agent.Squad.Id);
        }
        if (!state.CanScan || agent.Player.Physical.Sprinting) return false;
        direction = Quaternion.Euler(0, yaw, 0) * state.Forward;
        return true;
    }
}
