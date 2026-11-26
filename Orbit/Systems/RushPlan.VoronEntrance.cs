using System;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Settings;
using UnityEngine;

namespace Orbit.Systems;

internal sealed partial class RushPlan
{
    private VoronEntranceRoute _voron;
    private static Vector3 VoronHallHandle => VoronEntranceRoute.VoronHallHandle;
    private static bool IsVoronDoor(WaypointSystem w, Door door) =>
        door != null && door.Id == VoronEntranceRoute.MarkedDoorId
        && string.Equals(w.RecordedMap, "Shoreline", StringComparison.OrdinalIgnoreCase);

    private bool TryStartVoronEntrance(Squad squad, WaypointSystem w, Door door)
    {
        if (_inside || !_doorApproach || _voron != null || !IsVoronDoor(w, door)) return false;
        Release(squad, w);
        _route.Reset();
        _voron = new((leaf, feet) => DoorStep(leaf).InteractionCheck(feet));
        _nextWork = Time.time;
        Log.Info($"RUSH FALLBACK: {squad} site={Step} reason=direct approach failed from={_actor.Position}");
        return true;
    }

    private bool TickVoronEntrance(Squad squad, WaypointSystem w, RushPoint site, Door marked)
    {
        if (!w.TryOperationWork(this)) return true;
        _nextWork = Time.time + .25f;
        var previousEntrance = _voron.Entrance;
        var result = _voron.Tick(_actor, w, marked, _actor.Objective.Status is ObjectiveStatus.Failed or ObjectiveStatus.Finished);
        if (result == VoronEntranceRoute.Result.Waiting) return true;
        if (result == VoronEntranceRoute.Result.Failed) return Skip(squad, w, _voron.Detail);
        if (result == VoronEntranceRoute.Result.AtHandle)
        {
            _partial = false;
            return InteractDoor(squad, w, site, marked);
        }
        Release(squad, w);
        if (result == VoronEntranceRoute.Result.Retry)
        {
            Log.Info($"RUSH FALLBACK: {squad} site={Step} reason={_voron.Detail} entrance={previousEntrance?.Id} feet={_actor.Position}");
            return true;
        }
        Log.Info($"RUSH FALLBACK: {squad} site={Step} reason=entrance detour stage={_voron.Detail} entrance={_voron.Entrance?.Id} target={_voron.Target}");
        var target = _anchor = _voron.Target;
        SetOrder(_actor, Point(w, target, Step, DoorArrivalRadius), w);
        squad.Objective.Location = _orders[_actor];
        squad.Objective.Status = SquadObjectiveState.Active;
        foreach (var member in squad.Members)
            if (member != _actor && member.IsActive && !member.SoloExtractRequested && !CorpseEscort.InFlight(member))
                SetOrder(member, Point(w, target, "Follow rush"), w);
        if (result == VoronEntranceRoute.Result.Crossing) _actor.Objective.Status = ObjectiveStatus.Moving;
        return true;
    }
}
