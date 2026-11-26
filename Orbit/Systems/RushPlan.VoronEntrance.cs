using System;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Settings;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

internal sealed partial class RushPlan
{
    // Standing positions on the Shoreline power station mesh. Each pair straddles
    // one exterior door. The handle is on the hall side of the locked room.
    private static readonly (string Door, Vector3 Outside, Vector3 Hall)[] VoronEntrances =
    {
        ("door_Shoreline_Middle_00037", new(-223.50f, -40.90f, 182.50f), new(-223.875f, -40.65f, 184.875f)),
        ("door_Shoreline_Middle_00026", new(-218.75f, -40.88f, 194.125f), new(-221f, -40.64f, 193.75f)),
        ("door_Shoreline_Middle_00027", new(-240.375f, -40.94f, 184.75f), new(-240.875f, -40.66f, 187.125f))
    };
    private static readonly Vector3 VoronHallHandle = new(-241f, -40.66f, 197.625f);
    private static readonly Vector3[] VoronHandleStands =
    {
        VoronHallHandle, new(-240.8f, -40.66f, 197.4f), new(-240.8f, -40.66f, 197.8f)
    };
    private enum EntranceStage { Select, Outside, Cross, Handle, Arrived }
    private sealed class VoronRoute
    {
        internal EntranceStage Stage;
        internal bool AtHandle => Stage == EntranceStage.Arrived;
        internal int Next;
        internal bool TriedHallPath;
        internal Door Entrance;
        internal Vector3 Outside, Hall, Handle;
        internal float WaitUntil, NextOpen;
    }
    private VoronRoute _voron;

    private static bool IsVoronDoor(WaypointSystem w, Door door) =>
        door != null && door.Id == "door_Shoreline_Middle_00030"
        && string.Equals(w.RecordedMap, "Shoreline", StringComparison.OrdinalIgnoreCase);

    private bool TryStartVoronEntrance(Squad squad, WaypointSystem w, Door door)
    {
        if (_inside || !_doorApproach || _voron != null || !IsVoronDoor(w, door)) return false;
        Release(squad, w);
        _route.Reset();
        _voron = new();
        _nextWork = Time.time;
        Log.Info($"RUSH FALLBACK: {squad} site={Step} reason=direct approach failed from={_actor.Position}");
        return true;
    }

    private static bool VoronStand(Vector3 seed, out Vector3 stand)
    {
        var found = NavMesh.SamplePosition(seed, out var hit, .65f, NavMesh.AllAreas);
        stand = hit.position;
        return found && Mathf.Abs(stand.y - seed.y) <= .4f;
    }

    private bool VoronWalkable(Vector3 from, Vector3 to)
    {
        if ((from - to).sqrMagnitude <= DoorArrivalRadius * DoorArrivalRadius) return true;
        if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, _path)
            || _path.status != NavMeshPathStatus.PathComplete) return false;
        var corners = _path.corners;
        return corners != null && corners.Length > 0 && (corners[0] - from).sqrMagnitude <= .36f
            && (corners[corners.Length - 1] - to).sqrMagnitude <= .25f;
    }

    private bool FindVoronHandle(Door marked, out Vector3 stand)
    {
        foreach (var seed in VoronHandleStands)
            if (VoronStand(seed, out stand) && (marked.DoorState == EDoorState.Open || DoorStep(marked).InteractionCheck(stand)))
                return true;
        stand = default;
        return false;
    }

    private void OrderVoronLeg(Squad squad, WaypointSystem w, EntranceStage stage, Vector3 target)
    {
        Release(squad, w);
        _voron.Stage = stage;
        if (stage == EntranceStage.Handle) _voron.TriedHallPath = true;
        _anchor = target;
        SetOrder(_actor, Point(w, target, Step, DoorArrivalRadius), w);
        squad.Objective.Location = _orders[_actor];
        squad.Objective.Status = SquadObjectiveState.Active;
        foreach (var member in squad.Members)
            if (member != _actor && member.IsActive && !member.SoloExtractRequested && !CorpseEscort.InFlight(member))
                SetOrder(member, Point(w, target, "Follow rush"), w);
        Log.Info($"RUSH FALLBACK: {squad} site={Step} reason=entrance detour stage={stage} entrance={_voron.Entrance?.Id} target={target}");
    }

    private void NextVoronEntrance(Squad squad, WaypointSystem w, string reason)
    {
        Log.Info($"RUSH FALLBACK: {squad} site={Step} reason={reason} entrance={_voron.Entrance?.Id} feet={_actor.Position}");
        Release(squad, w);
        _voron.Stage = EntranceStage.Select;
        _voron.Entrance = null;
        _voron.WaitUntil = _voron.NextOpen = 0;
    }

    private bool TickVoronEntrance(Squad squad, WaypointSystem w, RushPoint site, Door marked)
    {
        var v = _voron;
        var feet = _actor.Position;
        var now = Time.time;
        if (!w.TryOperationWork(this)) return true;
        _nextWork = now + .25f;
        if (v.Stage == EntranceStage.Select)
        {
            if (!FindVoronHandle(marked, out v.Handle))
                return Skip(squad, w, "marked door inaccessible from hall");
            if (!v.TriedHallPath && VoronWalkable(feet, v.Handle))
            {
                OrderVoronLeg(squad, w, EntranceStage.Handle, v.Handle);
                return true;
            }
            if (v.Next >= VoronEntrances.Length) return Skip(squad, w, "no reachable building entrance");
            // At most one entrance per work slice. Both sides must lead somewhere
            // useful before we commit the squad to walking back around the facade.
            var entry = VoronEntrances[v.Next++];
            var door = w.RushDoor(entry.Door);
            if (door == null || (!door.Operatable && door.DoorState != EDoorState.Open) || door.DoorState is not (EDoorState.Open or EDoorState.Shut)
                || !VoronStand(entry.Outside, out v.Outside) || !VoronStand(entry.Hall, out v.Hall)
                || (door.DoorState == EDoorState.Shut && !DoorStep(door).InteractionCheck(v.Outside))
                || !VoronWalkable(feet, v.Outside) || !VoronWalkable(v.Hall, v.Handle)) return true;
            v.Entrance = door;
            OrderVoronLeg(squad, w, EntranceStage.Outside, v.Outside);
            return true;
        }

        if ((feet - _anchor).sqrMagnitude > .36f)
        {
            if (_actor.Objective.Status is ObjectiveStatus.Failed or ObjectiveStatus.Finished)
                NextVoronEntrance(squad, w, "movement stopped short");
            return true;
        }
        if (v.Stage == EntranceStage.Handle)
        {
            if (marked.DoorState != EDoorState.Open && !DoorStep(marked).InteractionCheck(feet))
                return Skip(squad, w, "marked door out of reach from hall");
            v.Stage = EntranceStage.Arrived;
            _partial = false;
            return InteractDoor(squad, w, site, marked);
        }
        if (v.Stage == EntranceStage.Cross)
        {
            if (VoronWalkable(feet, v.Handle)) OrderVoronLeg(squad, w, EntranceStage.Handle, v.Handle);
            else NextVoronEntrance(squad, w, "hall route changed");
            return true;
        }

        if (v.WaitUntil == 0) v.WaitUntil = now + 8f;
        if (v.Entrance == null || (!v.Entrance.Operatable && v.Entrance.DoorState != EDoorState.Open) || now >= v.WaitUntil)
        {
            NextVoronEntrance(squad, w, "entrance unavailable");
            return true;
        }
        if (v.Entrance.DoorState != EDoorState.Open)
        {
            if (v.Entrance.DoorState == EDoorState.Locked) NextVoronEntrance(squad, w, "entrance locked");
            else if (v.Entrance.DoorState == EDoorState.Shut && now >= v.NextOpen
                && DoorStep(v.Entrance).InteractionCheck(feet))
            {
                v.NextOpen = now + 2f;
                w.OpenRushDoor?.Invoke(_actor, v.Entrance);
            }
            return true;
        }
        if (VoronWalkable(feet, v.Hall)) OrderVoronLeg(squad, w, EntranceStage.Cross, v.Hall);
        else if (w.CrossVoronEntrance?.Invoke(_actor, v.Entrance, v.Hall) == true)
        {
            OrderVoronLeg(squad, w, EntranceStage.Cross, v.Hall);
            _actor.Objective.Status = ObjectiveStatus.Moving;
        }
        return true;
    }
}
